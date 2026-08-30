using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Csls.Tests;

/// <summary>
/// Verifies csls through a real Zed process and the csls Zed extension.
/// </summary>
[TestClass]
public sealed class ZedLanguageServerTests
{
    private static readonly TimeSpan s_workspaceStartupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens real framework metadata definitions as readable source documents in Zed.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task ZedOpensFrameworkDefinitionFromCsls()
    {
        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        (string DocumentText, string SymbolName, string ExpectedDeclaration)[] cases =
        [
            ("var awaitable = Task.CompletedTask.ConfigureAwait(false);", "ConfigureAwait", "class Task"),
            ("bool same = object.ReferenceEquals(null, null);", "ReferenceEquals", "class Object"),
            ("bool blank = string.IsNullOrWhiteSpace(null);", "IsNullOrWhiteSpace", "class String"),
            ("Dictionary<string, int> values = new();", "Dictionary", "class Dictionary")
        ];

        foreach ((string documentText, string symbolName, string expectedDeclaration) in cases)
        {
            await VerifyFrameworkDefinitionAsync(
                documentText,
                symbolName,
                expectedDeclaration).ConfigureAwait(false);
        }
    }

    private async Task VerifyFrameworkDefinitionAsync(
        string documentText,
        string symbolName,
        string expectedDeclaration)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        ArgumentNullException.ThrowIfNull(symbolName);
        ArgumentNullException.ThrowIfNull(expectedDeclaration);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string zedPath = EditorToolResolver.ResolveZed(repositoryRoot);
        string extensionPath = EditorToolResolver.ResolveCslsZedExtension(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-zed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            string userDataPath = Path.Join(fixturePath, "zed-data");
            string configurationPath = Path.Join(userDataPath, "config", "settings.json");
            string installedExtensionPath = Path.Join(
                userDataPath,
                "extensions",
                "installed",
                "csls");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            CopyDirectory(extensionPath, installedExtensionPath);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(launcherPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup =
                display.ConfigureAwait(false);
            string displayName = display.DisplayName;

            using Process zed = StartZed(
                zedPath,
                documentPath,
                FindPosition(documentText, symbolName),
                userDataPath,
                homePath,
                cachePath,
                displayName,
                workspacePath,
                workerPath);
            Task<string> zedOutputTask = zed.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> zedErrorTask = zed.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            int? serverProcessId = null;
            bool completed = false;
            try
            {
                ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                    workspacePath,
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                serverProcessId = session.ProcessId;
                var control = new ControlRpcClient(session.SocketPath);
                await using ConfiguredAsyncDisposable controlCleanup =
                    control.ConfigureAwait(false);
                ControlDashboardSnapshot initialSnapshot = await WaitForOpenDocumentAsync(
                    control,
                    documentPath,
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(initialSnapshot.Documents.Single(document =>
                    PathComparer.Equals(document.FilePath, documentPath)).IsOpen);

                X11Input.FocusWindow(displayName, "Program.cs");
                await control.StartTraceAsync(TestContext.CancellationToken).ConfigureAwait(false);
                ControlTraceInfo trace;
                try
                {
                    X11Input.SendControlSequence(displayName, 'k', 'i');
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/hover",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    X11Input.SendF12(displayName);
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/definition",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await WaitForTraceEntriesToSettleAsync(
                        control,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            "textDocument/hover",
                            "textDocument/definition"
                        },
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    trace = await control.StopTraceAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                AssertTraceSucceeded(trace, "textDocument/hover");
                AssertTraceSucceeded(trace, "textDocument/definition");
                ControlHoverResult hoverResult = await control.GetHoverAsync(
                    new ControlHoverRequest
                    {
                        DocumentPath = documentPath,
                        Position = FindPosition(documentText, symbolName)
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(hoverResult.Found);
                Assert.IsNotNull(hoverResult.Hover);
                Assert.Contains(symbolName, hoverResult.Hover.Contents.Value);

                IReadOnlyList<Location> definitions = await control.GetDefinitionAsync(
                    new ControlNavigationRequest
                    {
                        DocumentPath = documentPath,
                        Position = FindPosition(documentText, symbolName)
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Location definition = Assert.ContainsSingle(definitions);
                var definitionUri = new Uri(definition.Uri.ToString(), UriKind.Absolute);
                Assert.AreEqual(Uri.UriSchemeFile, definitionUri.Scheme);
                Assert.IsTrue(
                    File.Exists(definitionUri.LocalPath),
                    $"Materialized definition does not exist at {definitionUri.LocalPath}.");
                string materializedDefinitionText = await File.ReadAllTextAsync(
                    definitionUri.LocalPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains(expectedDeclaration, materializedDefinitionText, StringComparison.Ordinal);
                Assert.Contains(symbolName, materializedDefinitionText, StringComparison.Ordinal);

                string openedDefinitionText = await WaitForEditorTextAsync(
                    displayName,
                    expectedDeclaration,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains(
                    symbolName,
                    openedDefinitionText,
                    StringComparison.Ordinal,
                    "Zed opened the framework definition without its generated source text.");

                X11Input.SendControlCharacter(displayName, 'q');
                await zed.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, zed.ExitCode);
                completed = true;
            }
            finally
            {
                if (!zed.HasExited)
                {
                    zed.Kill(entireProcessTree: true);
                    await zed.WaitForExitAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                string zedOutput = await zedOutputTask.ConfigureAwait(false);
                string zedError = await zedErrorTask.ConfigureAwait(false);
                TestContext.WriteLine(zedOutput);
                TestContext.WriteLine(zedError);
                string zedLogPath = Path.Join(userDataPath, "logs", "Zed.log");
                if (!completed && File.Exists(zedLogPath))
                {
                    TestContext.WriteLine(await File.ReadAllTextAsync(
                        zedLogPath,
                        TestContext.CancellationToken).ConfigureAwait(false));
                }

                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps interactive navigation responsive while Zed diagnoses the real csls workspace.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task ZedProvidesInteractiveFeaturesInCslsWorkspace()
    {
        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string zedPath = EditorToolResolver.ResolveZed(repositoryRoot);
        string extensionPath = EditorToolResolver.ResolveCslsZedExtension(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        string documentPath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.Control.Server",
            "ControlService.cs");
        string definitionPath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.Control.Server",
            "ControlLogBuffer.cs");
        string documentText = await File.ReadAllTextAsync(
            documentPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        Position position = FindPosition(documentText, "ControlLogBuffer _logBuffer");
        HashSet<int> existingSessionProcessIds = GetExistingSessionProcessIds();

        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-zed-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string userDataPath = Path.Join(fixturePath, "zed-data");
            string configurationPath = Path.Join(userDataPath, "config", "settings.json");
            string installedExtensionPath = Path.Join(
                userDataPath,
                "extensions",
                "installed",
                "csls");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            CopyDirectory(extensionPath, installedExtensionPath);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(launcherPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup =
                display.ConfigureAwait(false);
            string displayName = display.DisplayName;

            using Process zed = StartZed(
                zedPath,
                documentPath,
                position,
                userDataPath,
                homePath,
                cachePath,
                displayName,
                repositoryRoot,
                workerPath);
            Task<string> zedOutputTask = zed.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> zedErrorTask = zed.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            int? serverProcessId = null;
            bool completed = false;
            try
            {
                ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                    repositoryRoot,
                    s_workspaceStartupTimeout,
                    TestContext.CancellationToken,
                    existingSessionProcessIds).ConfigureAwait(false);
                serverProcessId = session.ProcessId;
                var control = new ControlRpcClient(session.SocketPath);
                await using ConfiguredAsyncDisposable controlCleanup =
                    control.ConfigureAwait(false);
                await WaitForOpenDocumentAsync(
                    control,
                    documentPath,
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken).ConfigureAwait(false);

                X11Input.FocusWindow(displayName, "ControlService.cs");
                await control.StartTraceAsync(TestContext.CancellationToken).ConfigureAwait(false);
                ControlTraceInfo trace;
                try
                {
                    X11Input.SendControlSequence(displayName, 'k', 'i');
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/hover",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    X11Input.SendF12(displayName);
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/definition",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await WaitForOpenDocumentAsync(
                        control,
                        definitionPath,
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    X11Input.FocusWindow(displayName, "ControlLogBuffer.cs");
                    X11Input.SendFindAllReferences(displayName);
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/references",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await WaitForTraceEntriesToSettleAsync(
                        control,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            "textDocument/hover",
                            "textDocument/definition",
                            "textDocument/references"
                        },
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    trace = await control.StopTraceAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    foreach (ControlTraceEntry entry in trace.Entries.Where(static entry =>
                        !string.Equals(entry.Name, "workspace/inspect", StringComparison.Ordinal)))
                    {
                        TestContext.WriteLine(JsonSerializer.Serialize(entry));
                    }
                }

                AssertTraceSucceeded(trace, "textDocument/hover");
                AssertTraceSucceeded(trace, "textDocument/definition");
                AssertTraceSucceeded(trace, "textDocument/references");
                ControlTraceEntry definitionTrace = trace.Entries.First(entry =>
                    string.Equals(
                        entry.Name,
                        "textDocument/definition",
                        StringComparison.Ordinal) &&
                    string.Equals(entry.Status, "Succeeded", StringComparison.Ordinal));
                ControlTraceEntry openedDefinitionTrace = trace.Entries.First(entry =>
                    entry.Ordinal > definitionTrace.Ordinal &&
                    string.Equals(
                        entry.Name,
                        "textDocument/didOpen",
                        StringComparison.Ordinal));
                ControlTraceEntry referencesTrace = trace.Entries.First(entry =>
                    entry.Ordinal > openedDefinitionTrace.Ordinal &&
                    string.Equals(
                        entry.Name,
                        "textDocument/references",
                        StringComparison.Ordinal));
                Assert.AreEqual(
                    openedDefinitionTrace.WorkspaceGeneration,
                    referencesTrace.WorkspaceGeneration,
                    "Opening an unchanged on-disk definition in Zed advanced the semantic " +
                    "workspace generation and invalidated concurrent diagnostics.");
                ControlHoverResult hoverResult = await control.GetHoverAsync(
                    new ControlHoverRequest
                    {
                        DocumentPath = documentPath,
                        Position = position
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(hoverResult.Found);
                Assert.IsNotNull(hoverResult.Hover);
                Assert.Contains("ControlLogBuffer", hoverResult.Hover.Contents.Value);

                IReadOnlyList<Location> definitions = await control.GetDefinitionAsync(
                    new ControlNavigationRequest
                    {
                        DocumentPath = documentPath,
                        Position = position
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                string[] definitionPaths =
                [
                    .. definitions
                        .Where(definition => definition.Uri.ToString().StartsWith(
                            "file:",
                            StringComparison.Ordinal))
                        .Select(static definition => definition.Uri.GetFileSystemPath())
                ];
                Assert.Contains(
                    definitionPath,
                    definitionPaths,
                    $"Zed did not resolve ControlLogBuffer to {definitionPath}.");
                IReadOnlyList<Location> references = await control.GetReferencesAsync(
                    new ControlNavigationRequest
                    {
                        DocumentPath = documentPath,
                        Position = position,
                        IncludeDeclaration = true
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNotEmpty(references);

                X11Input.SendControlCharacter(displayName, 'q');
                await zed.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, zed.ExitCode);
                await AssertNoUnexpectedCslsZedLogsAsync(
                    userDataPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                completed = true;
            }
            finally
            {
                if (!zed.HasExited)
                {
                    zed.Kill(entireProcessTree: true);
                    await zed.WaitForExitAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                TestContext.WriteLine(await zedOutputTask.ConfigureAwait(false));
                TestContext.WriteLine(await zedErrorTask.ConfigureAwait(false));
                string zedLogPath = Path.Join(userDataPath, "logs", "Zed.log");
                if (!completed && File.Exists(zedLogPath))
                {
                    TestContext.WriteLine(await File.ReadAllTextAsync(
                        zedLogPath,
                        TestContext.CancellationToken).ConfigureAwait(false));
                }

                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refreshes a Zed workspace when a new compile item is created without restarting the editor.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task ZedRefreshesNewCompileItemsWithoutRestart()
    {
        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string zedPath = EditorToolResolver.ResolveZed(repositoryRoot);
        string extensionPath = EditorToolResolver.ResolveCslsZedExtension(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);

        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-zed-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            string addedDocumentPath = Path.Join(workspacePath, "X11Clipboard.cs");
            string documentText = "Console.WriteLine(X11Clipboard.Value);";
            string userDataPath = Path.Join(fixturePath, "zed-data");
            string configurationPath = Path.Join(userDataPath, "config", "settings.json");
            string installedExtensionPath = Path.Join(
                userDataPath,
                "extensions",
                "installed",
                "csls");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            CopyDirectory(extensionPath, installedExtensionPath);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                CreateFullWorkspaceSolution(repositoryRoot),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(launcherPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup = display.ConfigureAwait(false);
            using Process zed = StartZed(
                zedPath,
                documentPath,
                FindPosition(documentText, "X11Clipboard"),
                userDataPath,
                homePath,
                cachePath,
                display.DisplayName,
                workspacePath,
                workerPath);
            Task<string> zedOutputTask = zed.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> zedErrorTask = zed.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            int? serverProcessId = null;
            bool completed = false;
            try
            {
                ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                    workspacePath,
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                serverProcessId = session.ProcessId;
                var control = new ControlRpcClient(session.SocketPath);
                await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
                await WaitForOpenDocumentAsync(
                    control,
                    documentPath,
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken).ConfigureAwait(false);

                DocumentDiagnosticReport missing = await control.GetDiagnosticsAsync(
                    new ControlDiagnosticRequest { DocumentPath = documentPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains(
                    "CS0103",
                    missing.Items?.Select(static diagnostic => diagnostic.Code) ?? [],
                    "The fixture did not begin with the same unresolved-name diagnostic.");

                await control.StartTraceAsync(TestContext.CancellationToken).ConfigureAwait(false);
                ControlTraceInfo trace;
                try
                {
                    await File.WriteAllTextAsync(
                        addedDocumentPath,
                        "internal static class X11Clipboard { internal const string Value = \"ready\"; }",
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await WaitForDiagnosticToClearAsync(
                        control,
                        documentPath,
                        "CS0103",
                        TimeSpan.FromSeconds(5),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    trace = await control.StopTraceAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                string[] traceNames = [.. trace.Entries.Select(static entry => entry.Name)];
                Assert.Contains(
                    "workspace/didChangeWatchedFiles",
                    traceNames,
                    "Zed did not notify csls that X11Clipboard.cs was created.");

                X11Input.SendControlCharacter(display.DisplayName, 'q');
                await zed.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, zed.ExitCode);
                completed = true;
            }
            finally
            {
                if (!zed.HasExited)
                {
                    zed.Kill(entireProcessTree: true);
                    await zed.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                }

                TestContext.WriteLine(await zedOutputTask.ConfigureAwait(false));
                TestContext.WriteLine(await zedErrorTask.ConfigureAwait(false));
                string zedLogPath = Path.Join(userDataPath, "logs", "Zed.log");
                if (!completed && File.Exists(zedLogPath))
                {
                    TestContext.WriteLine(await File.ReadAllTextAsync(
                        zedLogPath,
                        TestContext.CancellationToken).ConfigureAwait(false));
                }

                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string CreateFullWorkspaceSolution(string repositoryRoot)
    {
        var repositorySolution = XDocument.Load(Path.Join(repositoryRoot, "Csls.slnx"));
        XElement[] projects =
        [
            .. repositorySolution
                .Descendants("Project")
                .Select(project => project.Attribute("Path")?.Value)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new XElement(
                    "Project",
                    new XAttribute("Path", Path.GetFullPath(path!, repositoryRoot))))
        ];
        var solution = new XDocument(
            new XElement(
                "Solution",
                new XElement("Folder", new XAttribute("Name", "/csls/"), projects),
                new XElement("Project", new XAttribute("Path", "Fixture.csproj"))));
        return solution.ToString(SaveOptions.DisableFormatting);
    }

    private static Process StartZed(
        string zedPath,
        string documentPath,
        Position position,
        string userDataPath,
        string homePath,
        string cachePath,
        string displayName,
        string workspacePath,
        string workerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = zedPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("--foreground");
        startInfo.ArgumentList.Add("--user-data-dir");
        startInfo.ArgumentList.Add(userDataPath);
        startInfo.ArgumentList.Add(workspacePath);
        startInfo.ArgumentList.Add(
            $"{documentPath}:{position.Line + 1}:{position.Character + 1}");
        startInfo.Environment["DISPLAY"] = displayName;
        startInfo.Environment["CSLS_WORKER_PATH"] = workerPath;
        startInfo.Environment["HOME"] = homePath;
        startInfo.Environment["NO_AT_BRIDGE"] = "1";
        startInfo.Environment.Remove("WAYLAND_DISPLAY");
        startInfo.Environment["XDG_CACHE_HOME"] = cachePath;
        startInfo.Environment["XDG_SESSION_TYPE"] = "x11";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Zed did not start.");
    }

    private static Position FindPosition(string text, string value)
    {
        int offset = text.IndexOf(value, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidOperationException($"The source text does not contain '{value}'.");
        }

        string precedingText = text[..offset];
        int line = precedingText.Count(static character => character == '\n');
        int previousLineBreak = precedingText.LastIndexOf('\n');
        return new Position(line, offset - previousLineBreak - 1);
    }

    private static HashSet<int> GetExistingSessionProcessIds()
    {
        string socketDirectory = ControlEndpoint.GetSocketDirectory();
        if (!Directory.Exists(socketDirectory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(socketDirectory, "*.csls.socket")
                .Select(Path.GetFileName)
                .Where(static fileName => fileName is not null)
                .Select(static fileName => fileName!.Split('.', 2)[0])
                .Select(static processIdText => int.TryParse(
                    processIdText,
                    out int processId)
                        ? processId
                        : (int?)null)
                .Where(static processId => processId.HasValue)
                .Select(static processId => processId!.Value)
        ];
    }

    private static void AssertTraceSucceeded(ControlTraceInfo trace, string requestName)
    {
        ControlTraceEntry[] completedRequests =
        [
            .. trace.Entries.Where(entry => string.Equals(
                entry.Name,
                requestName,
                StringComparison.Ordinal) &&
                entry.CompletedAt.HasValue)
        ];
        Assert.IsNotEmpty(
            completedRequests,
            $"Zed did not complete {requestName} through csls.");
        foreach (ControlTraceEntry request in completedRequests)
        {
            Assert.AreEqual("Succeeded", request.Status);
            Assert.IsNull(request.ExceptionType);
        }
    }

    private static async Task AssertNoUnexpectedCslsZedLogsAsync(
        string userDataPath,
        CancellationToken cancellationToken)
    {
        string zedLogPath = Path.Join(userDataPath, "logs", "Zed.log");
        Assert.IsTrue(File.Exists(zedLogPath), $"Zed did not persist its log at {zedLogPath}.");
        string[] unexpectedEntries =
        [
            .. (await File.ReadAllLinesAsync(zedLogPath, cancellationToken).ConfigureAwait(false))
                .Where(static line =>
                    (line.Contains(" WARN ", StringComparison.Ordinal) ||
                        line.Contains(" ERROR ", StringComparison.Ordinal)) &&
                    (line.Contains(" via csls ", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains(
                            "workspace diagnostics",
                            StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("/metadata/", StringComparison.Ordinal)))
        ];
        Assert.IsEmpty(
            unexpectedEntries,
            $"Zed logged unexpected CSLS warnings or errors:{Environment.NewLine}" +
            string.Join(Environment.NewLine, unexpectedEntries));
    }

    private static async Task<ControlDashboardSnapshot> WaitForOpenDocumentAsync(
        ControlRpcClient control,
        string documentPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        ControlDashboardSnapshot? lastSnapshot = null;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                lastSnapshot = snapshot;
                if (snapshot.Documents.Any(document =>
                    document.IsOpen && PathComparer.Equals(document.FilePath, documentPath)))
                {
                    return snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string openDocuments = string.Join(
                Environment.NewLine,
                lastSnapshot?.Documents
                    .Where(static document => document.IsOpen)
                    .Select(static document => document.FilePath ?? document.Name) ?? []);
            throw new TimeoutException(
                $"Zed did not open {documentPath} through csls. Open documents:" +
                $"{Environment.NewLine}{openDocuments}");
        }

        throw new InvalidOperationException("The open-document polling loop ended unexpectedly.");
    }

    private static async Task<ControlTraceInfo> WaitForTraceEntryAsync(
        ControlRpcClient control,
        string requestName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                if (snapshot.Requests.Trace.Entries.Any(entry =>
                    string.Equals(entry.Name, requestName, StringComparison.Ordinal) &&
                    entry.CompletedAt.HasValue))
                {
                    return snapshot.Requests.Trace;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Zed did not complete {requestName} through csls.");
        }

        throw new InvalidOperationException("The trace polling loop ended unexpectedly.");
    }

    private static async Task WaitForTraceEntriesToSettleAsync(
        ControlRpcClient control,
        HashSet<string> requestNames,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        int settledSnapshots = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                bool hasRunningRequest = snapshot.Requests.Trace.Entries.Any(entry =>
                    requestNames.Contains(entry.Name) &&
                    !entry.CompletedAt.HasValue);
                settledSnapshots = hasRunningRequest ? 0 : settledSnapshots + 1;
                if (settledSnapshots == 2)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Zed did not complete all interactive requests through csls.");
        }

        throw new InvalidOperationException("The trace settling loop ended unexpectedly.");
    }

    private static async Task WaitForDiagnosticToClearAsync(
        ControlRpcClient control,
        string documentPath,
        string diagnosticCode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        DocumentDiagnosticReport? lastReport = null;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                lastReport = await control.GetDiagnosticsAsync(
                    new ControlDiagnosticRequest { DocumentPath = documentPath },
                    timeoutSource.Token).ConfigureAwait(false);
                if (!(lastReport.Items?.Any(diagnostic =>
                        string.Equals(diagnostic.Code, diagnosticCode, StringComparison.Ordinal)) ?? false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string remainingCodes = string.Join(
                ", ",
                lastReport?.Items?.Select(static diagnostic => diagnostic.Code) ?? []);
            throw new TimeoutException(
                $"Zed did not clear {diagnosticCode} after creating a compile item. " +
                $"Remaining diagnostics: {remainingCodes}");
        }

        throw new InvalidOperationException("The diagnostic polling loop ended unexpectedly.");
    }

    private static async Task<string> WaitForEditorTextAsync(
        string displayName,
        string expectedText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        string clipboardText = string.Empty;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                X11Input.SendControlCharacter(displayName, 'a');
                X11Input.SendControlCharacter(displayName, 'c');
                clipboardText = await X11Clipboard.ReadTextAsync(
                    displayName,
                    timeoutSource.Token).ConfigureAwait(false);
                if (clipboardText.Contains(expectedText, StringComparison.Ordinal))
                {
                    return clipboardText;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Zed did not open editor text containing '{expectedText}'. " +
                $"Last copied text: {clipboardText}");
        }

        throw new InvalidOperationException("The editor-text polling loop ended unexpectedly.");
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (string directoryPath in Directory.EnumerateDirectories(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(
                destinationPath,
                Path.GetRelativePath(sourcePath, directoryPath)));
        }

        foreach (string filePath in Directory.EnumerateFiles(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            File.Copy(
                filePath,
                Path.Join(destinationPath, Path.GetRelativePath(sourcePath, filePath)));
        }
    }

    private static string CreateConfiguration(string launcherPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            {
              "auto_install_extensions": {
                "csls": false,
                "csharp": false,
                "html": false
              },
              "auto_update": false,
              "languages": {
                "CSharp": {
                  "language_servers": ["csls"]
                }
              },
              "lsp": {
                "csls": {
                  "binary": {
                    "path": {{ToJsonString(dotnetPath)}},
                    "arguments": [{{ToJsonString(launcherPath)}}, "lsp"]
                  },
                  "settings": {
                    "enableAnalyzers": true,
                    "configuration": "Debug"
                  }
                }
              },
              "session": {
                "trust_all_worktrees": true
              },
              "telemetry": {
                "diagnostics": false,
                "metrics": false
              }
            }
            """;
    }

    private static string ToJsonString(string value) =>
        $"\"{JsonEncodedText.Encode(value)}\"";
}
