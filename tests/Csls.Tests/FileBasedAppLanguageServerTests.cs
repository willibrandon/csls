using Csls.Protocol;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Csls.Tests;

/// <summary>
/// Verifies SDK-backed file-based apps through a real language-server process.
/// </summary>
[TestClass]
public sealed class FileBasedAppLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves SDK, property, include, project, and package directives through the selected SDK.
    /// </summary>
    [TestMethod]
    public async Task DirectivesResolveThroughRealSdkWorkspace()
    {
        string fixturePath = CreateFixturePath("directives");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string feedPath = Path.Join(fixturePath, "feed");
            string packagePath = Path.Join(fixturePath, "Package");
            string projectPath = Path.Join(fixturePath, "Referenced");
            Directory.CreateDirectory(feedPath);
            Directory.CreateDirectory(packagePath);
            Directory.CreateDirectory(projectPath);
            File.Copy(
                Path.Join(EditorToolResolver.FindRepositoryRoot(), "global.json"),
                Path.Join(fixturePath, "global.json"));
            await WriteNuGetConfigurationAsync(
                fixturePath,
                feedPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(packagePath, "Fixture.Package.csproj"),
                PackageProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(packagePath, "PackageValue.cs"),
                PackageDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RunDotNetAsync(
                fixturePath,
                [
                    "pack",
                    Path.Join(packagePath, "Fixture.Package.csproj"),
                    "--configuration",
                    "Release",
                    "--nologo",
                    "--output",
                    feedPath
                ],
                TestContext.CancellationToken).ConfigureAwait(false);

            string referencedDocumentPath = Path.Join(projectPath, "ProjectValue.cs");
            await File.WriteAllTextAsync(
                Path.Join(projectPath, "Referenced.csproj"),
                ReferencedProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                referencedDocumentPath,
                ReferencedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string includedDocumentPath = Path.Join(fixturePath, "Included.cs");
            await File.WriteAllTextAsync(
                includedDocumentPath,
                IncludedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string entryPointPath = Path.Join(fixturePath, "App.cs");
            await File.WriteAllTextAsync(
                entryPointPath,
                FileBasedAppText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await StartWorkerAsync(fixturePath)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                entryPointPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(initialization.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
            Assert.IsFalse(File.Exists(entryPointPath + ".csproj"));
            await lsp.OpenDocumentAsync(entryPointPath, FileBasedAppText).ConfigureAwait(false);

            IReadOnlyList<Location> includedDefinitions = await lsp.RequestDefinitionsAsync(
                entryPointPath,
                new Position(11, 32),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                includedDocumentPath,
                Assert.ContainsSingle(includedDefinitions).Uri.GetFileSystemPath());
            IReadOnlyList<Location> projectDefinitions = await lsp.RequestDefinitionsAsync(
                entryPointPath,
                new Position(11, 69),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                referencedDocumentPath,
                Assert.ContainsSingle(projectDefinitions).Uri.GetFileSystemPath());

            JsonElement? packageHover = await lsp.RequestHoverAsync(
                entryPointPath,
                new Position(11, 105),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(packageHover.HasValue);
            Assert.Contains("PackageValue", packageHover.Value.ToString(), StringComparison.Ordinal);
            DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
                entryPointPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(diagnostics.Items);
            string? compilerDiagnostic = diagnostics.Items
                .Select(static diagnostic => diagnostic.Code)
                .FirstOrDefault(static code => code?.StartsWith(
                    "CS",
                    StringComparison.Ordinal) == true);
            Assert.IsNull(compilerDiagnostic);
            Assert.IsFalse(File.Exists(entryPointPath + ".csproj"));

            string standardError = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Discovers shebang and directive file-based apps beneath a normal editor workspace folder.
    /// </summary>
    [TestMethod]
    public async Task FileBasedAppsAreDiscoveredFromWorkspaceFolder()
    {
        string fixturePath = CreateFixturePath("discovery");
        string scriptPath = Path.Join(fixturePath, "tools", "hello.cs");
        string directiveAppPath = Path.Join(fixturePath, "tools", "directives.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                DiscoveredAppText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                directiveAppPath,
                DiscoveredDirectiveAppText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await StartWorkerAsync(fixturePath)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(scriptPath + ".csproj"));
            await lsp.OpenDocumentAsync(scriptPath, DiscoveredAppText).ConfigureAwait(false);
            JsonElement? hover = await lsp.RequestHoverAsync(
                scriptPath,
                new Position(1, 1),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(hover.HasValue);
            Assert.Contains("System.Console", hover.Value.ToString(), StringComparison.Ordinal);
            await lsp.OpenDocumentAsync(
                directiveAppPath,
                DiscoveredDirectiveAppText).ConfigureAwait(false);
            JsonElement? directiveHover = await lsp.RequestHoverAsync(
                directiveAppPath,
                new Position(1, 1),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(directiveHover.HasValue);
            Assert.Contains(
                "System.Console",
                directiveHover.Value.ToString(),
                StringComparison.Ordinal);

            CSharpWorkspaceInfo workspace = await lsp.RequestWorkspaceInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            CSharpWorkspaceFolderInfo folder = Assert.ContainsSingle(workspace.Workspaces);
            Assert.AreEqual(fixturePath, folder.RootPath);
            Assert.AreEqual(2, folder.ProjectCount);
            CSharpWorkspaceProjectInfo scriptProject = Assert.ContainsSingle(
                workspace.Projects.Where(project => string.Equals(
                    project.FilePath,
                    scriptPath,
                    StringComparison.Ordinal)));
            CSharpWorkspaceProjectInfo directiveProject = Assert.ContainsSingle(
                workspace.Projects.Where(project => string.Equals(
                    project.FilePath,
                    directiveAppPath,
                    StringComparison.Ordinal)));
            Assert.AreEqual("hello.cs", scriptProject.Name);
            Assert.AreEqual("directives.cs", directiveProject.Name);

            string standardError = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(scriptPath + ".csproj"));
            Assert.IsFalse(File.Exists(directiveAppPath + ".csproj"));
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps generated file-app projects invisible to editor workspace watchers.
    /// </summary>
    [TestMethod]
    public async Task FileBasedAppProjectIsNeverExposedToWorkspaceWatchers()
    {
        string fixturePath = CreateFixturePath("workspace-watcher");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string entryPointPath = Path.Join(fixturePath, "App.cs");
            string markerPath = Path.Join(fixturePath, "workspace-ready.marker");
            await File.WriteAllTextAsync(
                entryPointPath,
                DiscoveredDirectiveAppText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var observedPaths = new ConcurrentQueue<string>();
            var markerObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new System.IO.FileSystemWatcher(fixturePath)
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
            };
            watcher.Created += (_, eventArgs) =>
            {
                observedPaths.Enqueue(eventArgs.FullPath);
                if (string.Equals(eventArgs.FullPath, markerPath, PathComparison))
                {
                    markerObserved.TrySetResult();
                }
            };

            LspProcessSession lsp = await StartWorkerAsync(fixturePath)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            CSharpWorkspaceInfo workspace = await lsp.RequestWorkspaceInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            CSharpWorkspaceFolderInfo folder = Assert.ContainsSingle(workspace.Workspaces);
            Assert.AreEqual(1, folder.ProjectCount);
            await File.WriteAllTextAsync(
                markerPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            await markerObserved.Task.WaitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);

            string[] generatedProjects =
            [
                .. observedPaths.Where(static path => path.EndsWith(
                    ".cs.csproj",
                    StringComparison.OrdinalIgnoreCase))
            ];
            Assert.IsEmpty(
                generatedProjects,
                "File-app loading exposed generated projects to workspace watchers: " +
                string.Join(", ", generatedProjects));

            string standardError = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads the repository file app and its real project-reference graph without diagnostics.
    /// </summary>
    [TestMethod]
    public async Task RepositoryFileBasedAppLoadsWithoutWorkspaceWarningsOrErrors()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string entryPointPath = Path.Join(repositoryRoot, "scripts", "Generate-Docs.cs");
        LspProcessSession lsp = await StartWorkerAsync(repositoryRoot)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            entryPointPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.CompleteInitializationAsync().ConfigureAwait(false);
        CSharpWorkspaceInfo workspace = await lsp.RequestWorkspaceInfoAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        CSharpWorkspaceFolderInfo folder = Assert.ContainsSingle(workspace.Workspaces);
        Assert.IsGreaterThan(1, folder.ProjectCount);
        DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
            entryPointPath,
            previousResultId: null,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics.Items ?? []);

        string standardError = await lsp.ShutdownAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("warn:", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail:", standardError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recovers a generated file-app project left behind when a previous host was terminated.
    /// </summary>
    [TestMethod]
    public async Task StaleGeneratedProjectDoesNotBlockFileBasedApp()
    {
        string fixturePath = CreateFixturePath("stale-project");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string entryPointPath = Path.Join(fixturePath, "App.cs");
            string generatedProjectPath = entryPointPath + ".csproj";
            await File.WriteAllTextAsync(
                entryPointPath,
                DiscoveredDirectiveAppText,
                TestContext.CancellationToken).ConfigureAwait(false);
            var staleProject = new XDocument(
                new XElement(
                    "Project",
                    new XElement(
                        "PropertyGroup",
                        new XElement("FileBasedProgram", "true"),
                        new XElement("EntryPointFilePath", entryPointPath))));
            await File.WriteAllTextAsync(
                generatedProjectPath,
                staleProject.ToString(),
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await StartWorkerAsync(fixturePath)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(
                entryPointPath,
                DiscoveredDirectiveAppText).ConfigureAwait(false);

            JsonElement? hover = await lsp.RequestHoverAsync(
                entryPointPath,
                new Position(1, 1),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(hover.HasValue);
            Assert.Contains("System.Console", hover.Value.ToString(), StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(generatedProjectPath));

            string standardError = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<LspProcessSession> StartWorkerAsync(string workingDirectory)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return await LspProcessSession.StartAsync(
            "csls-file-based-app-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            workingDirectory).ConfigureAwait(false);
    }

    private static string CreateFixturePath(string name) => Path.Join(
        Path.GetTempPath(),
        $"csls-file-app-{name}-{Guid.NewGuid():N}");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static async Task WriteNuGetConfigurationAsync(
        string fixturePath,
        string feedPath,
        CancellationToken cancellationToken)
    {
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "fixture"),
                        new XAttribute("value", feedPath)))));
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "nuget.config"),
            document.ToString(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test .NET process did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', arguments)} failed with exit code " +
                $"{process.ExitCode}:{Environment.NewLine}{error}{Environment.NewLine}{output}");
        }
    }

    private const string PackageProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>Csls.FileAppFixture</PackageId>
            <Version>1.0.0</Version>
            <Authors>csls</Authors>
          </PropertyGroup>
        </Project>
        """;

    private const string PackageDocumentText = """
        namespace Fixture.Package;

        public static class PackageValue
        {
            public const string Text = "package";
        }
        """;

    private const string ReferencedProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string ReferencedDocumentText = """
        namespace Fixture.Project;

        public static class ProjectValue
        {
            public const string Text = "project";
        }
        """;

    private const string IncludedDocumentText = """
        namespace Fixture.App;

        public static class IncludedValue
        {
            public const string Text = "included";
        }
        """;

    private const string FileBasedAppText = """
        #!/usr/bin/env dotnet
        #:sdk Microsoft.NET.Sdk.Web
        #:property TargetFramework=net10.0
        #:property DefineConstants=FILE_APP_PROPERTY
        #:include Included.cs
        #:project Referenced/Referenced.csproj
        #:package Csls.FileAppFixture@1.0.0
        #if !FILE_APP_PROPERTY
        #error The property directive was not applied.
        #endif

        Console.WriteLine(Fixture.App.IncludedValue.Text + Fixture.Project.ProjectValue.Text + Fixture.Package.PackageValue.Text);
        """;

    private const string DiscoveredAppText = """
        #!/usr/bin/env dotnet
        Console.WriteLine("hello");
        """;

    private const string DiscoveredDirectiveAppText = """
        #:property TargetFramework=net10.0
        Console.WriteLine("hello from a directive app");
        """;
}
