using Csls.Control;
using Csls.Control.Contracts;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies csls through a real Visual Studio Code extension host.
/// </summary>
[TestClass]
public sealed class VsCodeLanguageServerTests
{
    private static readonly TimeSpan s_editorStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] s_nestedLogLevelMarkers =
    [
        " [trace] trce:",
        " [debug] dbug:",
        " [info] info:",
        " [warning] warn:",
        " [error] fail:",
        " [error] crit:"
    ];

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runs the complete C# feature contract in a real desktop extension host.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public Task VsCodeDesktopHostProvidesCSharpLanguageFeatures() =>
        RunVsCodeHostAsync(remote: false);

    /// <summary>
    /// Enables visible C# semantic highlighting when the active theme does not opt in.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public Task VsCodeHostEnablesSemanticHighlightingWithoutThemeOptIn() =>
        RunVsCodeHostAsync(
            remote: false,
            localSuite: "dist/semantic-highlighting-suite.cjs");

    /// <summary>
    /// Stops automatic test discovery and every process it started when VS Code shuts down.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task VsCodeHostStopsAutomaticTestDiscoveryProcessesOnShutdown()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string runnerPath = Path.Join(repositoryRoot, "tests", "vscode", "runner.mjs");
        string runtimeExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-dotnet-runtime",
            platformSpecific: false);
        string csharpExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-csharp",
            platformSpecific: true);
        string runId = Guid.NewGuid().ToString("N")[..16];
        string fixturePath = Path.Join(Path.GetTempPath(), $"cv-shutdown-{runId}");
        string workspacePath = Path.Join(fixturePath, "workspace");
        string testProjectPath = Path.Join(workspacePath, "Tests");
        string userDataPath = Path.Join(fixturePath, "u");
        string extensionsPath = Path.Join(fixturePath, "extensions");
        string remoteDataPath = Path.Join(fixturePath, "remote");
        Directory.CreateDirectory(testProjectPath);
        Directory.CreateDirectory(userDataPath);
        Directory.CreateDirectory(remoteDataPath);
        int? blockedProcessId = null;
        try
        {
            string settingsPath = Path.Join(userDataPath, "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            string debuggerPath = await VsCodeDebuggerFixture.ExtractAsync(
                csharpExtensionPath,
                Path.Join(fixturePath, "debugger"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                settingsPath,
                CreateSettingsText(debuggerPath),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                "<Solution><Project Path=\"Tests/Fixture.Tests.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(testProjectPath, "Fixture.Tests.csproj"),
                ShutdownTestProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string blockingScriptPath = Path.Join(testProjectPath, "block-discovery.sh");
            await File.WriteAllTextAsync(
                blockingScriptPath,
                "#!/bin/sh\n" +
                "printf '%s\\n' \"$$\" > \"$1\"\n" +
                ": > \"$2\"\n" +
                "process=\"$PPID\"\n" +
                "while [ \"$process\" -gt 1 ]; do\n" +
                "  tr '\\000' ' ' < \"/proc/$process/cmdline\" >> \"$2\"\n" +
                "  printf '\\n' >> \"$2\"\n" +
                "  process=\"$(awk '/^PPid:/ { print $2 }' \"/proc/$process/status\")\"\n" +
                "done\n" +
                "exec sleep 300\n",
                TestContext.CancellationToken).ConfigureAwait(false);
            string extensionPath = await VsCodeExtensionPackage.GetAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup = display.ConfigureAwait(false);
            await RunVsCodeAsync(
                repositoryRoot,
                runnerPath,
                extensionPath,
                runtimeExtensionPath,
                workspacePath,
                userDataPath,
                extensionsPath,
                remoteServerRoot: null,
                remoteDataPath,
                display.DisplayName,
                localSuite: "dist/shutdown-suite.cjs").ConfigureAwait(false);

            string processIdPath = Path.Join(testProjectPath, "discovery.pid");
            Assert.IsTrue(File.Exists(processIdPath), "The blocking discovery process did not start.");
            string processTreePath = Path.Join(testProjectPath, "discovery-process-tree.txt");
            Assert.IsTrue(File.Exists(processTreePath), "The discovery process ancestry was not recorded.");
            string discoveryProcessTree = await File.ReadAllTextAsync(
                processTreePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("--disable-build-servers", discoveryProcessTree);
            Assert.Contains("--maxcpucount:1", discoveryProcessTree);
            Assert.Contains("-property:UseSharedCompilation=false", discoveryProcessTree);
            blockedProcessId = int.Parse(
                await File.ReadAllTextAsync(processIdPath, TestContext.CancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            await ProcessExitWaiter.WaitAsync(
                blockedProcessId.Value,
                TimeSpan.FromSeconds(5),
                TestContext.CancellationToken).ConfigureAwait(false);
            blockedProcessId = null;
        }
        finally
        {
            if (blockedProcessId is int processId)
            {
                using Process? process = TryGetProcessById(processId);
                if (process is not null)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the complete C# feature contract in a real remote extension host.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public Task VsCodeRemoteHostProvidesCSharpLanguageFeatures()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("CSLS_RUN_VSCODE_REMOTE_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                "Set CSLS_RUN_VSCODE_REMOTE_TESTS=true to run the remote VS Code host.");
        }

        return RunVsCodeHostAsync(remote: true);
    }

    /// <summary>
    /// Opens the real csls repository in a remote VS Code host without warnings or errors.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task VsCodeRemoteHostLoadsCslsWorkspaceWithoutWarningsOrErrors()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("CSLS_RUN_VSCODE_REMOTE_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                "Set CSLS_RUN_VSCODE_REMOTE_TESTS=true to run the remote VS Code host.");
        }
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string runnerPath = Path.Join(repositoryRoot, "tests", "vscode", "runner.mjs");
        Assert.IsTrue(File.Exists(runnerPath), $"VS Code runner not found at {runnerPath}.");
        string testElectronPath = Path.Join(
            repositoryRoot,
            "tests",
            "vscode",
            "node_modules",
            "@vscode",
            "test-electron",
            "package.json");
        Assert.IsTrue(
            File.Exists(testElectronPath),
            "The VS Code fixture is not provisioned. Run scripts/Provision-VsCode.cs.");
        string runtimeExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-dotnet-runtime",
            platformSpecific: false);
        string csharpExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-csharp",
            platformSpecific: true);
        string remoteServerRoot = EditorToolResolver.ResolveVsCodeRemoteServerRoot(repositoryRoot);

        string runId = Guid.NewGuid().ToString("N")[..16];
        string fixturePath = Path.Join(Path.GetTempPath(), $"cv-repository-{runId}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string userDataPath = Path.Join(fixturePath, "u");
            string extensionsPath = Path.Join(fixturePath, "extensions");
            string remoteDataPath = Path.Join(fixturePath, "remote");
            Directory.CreateDirectory(userDataPath);
            Directory.CreateDirectory(remoteDataPath);
            string settingsPath = Path.Join(userDataPath, "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            string debuggerPath = await VsCodeDebuggerFixture.ExtractAsync(
                csharpExtensionPath,
                Path.Join(fixturePath, "debugger"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                settingsPath,
                CreateSettingsText(debuggerPath),
                TestContext.CancellationToken).ConfigureAwait(false);
            string extensionPath = await VsCodeExtensionPackage.GetAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup = display.ConfigureAwait(false);
            await RunVsCodeAsync(
                repositoryRoot,
                runnerPath,
                extensionPath,
                runtimeExtensionPath,
                repositoryRoot,
                userDataPath,
                extensionsPath,
                remoteServerRoot,
                remoteDataPath,
                display.DisplayName,
                remoteSuite: "./dist/startup-suite.cjs",
                runTimeout: TimeSpan.FromMinutes(5),
                trackControlSession: false).ConfigureAwait(false);

            await AssertNoUnexpectedCslsOutputAsync(
                [userDataPath, remoteDataPath],
                expectWorkspaceRestore: false,
                TestContext.CancellationToken,
                requiredRestoredEntryPointFileName: "Generate-Docs.cs").ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task RunVsCodeHostAsync(bool remote, string? localSuite = null)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string runnerPath = Path.Join(repositoryRoot, "tests", "vscode", "runner.mjs");
        Assert.IsTrue(File.Exists(runnerPath), $"VS Code runner not found at {runnerPath}.");
        string testElectronPath = Path.Join(
            repositoryRoot,
            "tests",
            "vscode",
            "node_modules",
            "@vscode",
            "test-electron",
            "package.json");
        Assert.IsTrue(
            File.Exists(testElectronPath),
            "The VS Code fixture is not provisioned. Run scripts/Provision-VsCode.cs.");
        string runtimeExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-dotnet-runtime",
            platformSpecific: false);
        string csharpExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-csharp",
            platformSpecific: true);
        string? remoteServerRoot = remote
            ? EditorToolResolver.ResolveVsCodeRemoteServerRoot(repositoryRoot)
            : null;

        string runId = Guid.NewGuid().ToString("N")[..16];
        string fixturePath = Path.Join(Path.GetTempPath(), $"cv-{runId}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string userDataPath = Path.Join(fixturePath, "u");
            string extensionsPath = Path.Join(fixturePath, "extensions");
            string remoteDataPath = Path.Join(fixturePath, "remote");
            Directory.CreateDirectory(workspacePath);
            string sourceProjectPath = Path.Join(workspacePath, "App");
            string testProjectPath = Path.Join(workspacePath, "Tests");
            string toolsPath = Path.Join(workspacePath, "Tools");
            Directory.CreateDirectory(sourceProjectPath);
            Directory.CreateDirectory(testProjectPath);
            Directory.CreateDirectory(toolsPath);
            Directory.CreateDirectory(userDataPath);
            Directory.CreateDirectory(remoteDataPath);
            string settingsPath = Path.Join(userDataPath, "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            string debuggerPath = await VsCodeDebuggerFixture.ExtractAsync(
                csharpExtensionPath,
                Path.Join(fixturePath, "debugger"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                settingsPath,
                CreateSettingsText(debuggerPath),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(sourceProjectPath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(testProjectPath, "Fixture.Tests.csproj"),
                TestProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                SolutionText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Calculator.cs"),
                CalculatorDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(testProjectPath, "ExampleTests.cs"),
                TestDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(toolsPath, "Tool.cs"),
                FileBasedAppText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string extensionPath = await VsCodeExtensionPackage.GetAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux())
            {
                XDisplaySession display = await XDisplaySession.StartAsync(
                    TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await using ConfiguredAsyncDisposable displayCleanup =
                    display.ConfigureAwait(false);
                await RunVsCodeAsync(
                    repositoryRoot,
                    runnerPath,
                    extensionPath,
                    runtimeExtensionPath,
                    workspacePath,
                    userDataPath,
                    extensionsPath,
                    remoteServerRoot,
                    remoteDataPath,
                    display.DisplayName,
                    localSuite: localSuite).ConfigureAwait(false);
            }
            else
            {
                await RunVsCodeAsync(
                    repositoryRoot,
                    runnerPath,
                    extensionPath,
                    runtimeExtensionPath,
                    workspacePath,
                    userDataPath,
                    extensionsPath,
                    remoteServerRoot,
                    remoteDataPath,
                    displayName: null,
                    localSuite: localSuite).ConfigureAwait(false);
            }

            await AssertNoUnexpectedCslsOutputAsync(
                [userDataPath, remoteDataPath],
                expectWorkspaceRestore: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(Path.Join(repositoryRoot, "TestResults")));
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
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="../Calculator.cs" />
            <Compile Include="../Program.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string DocumentText = """
        Console.WriteLine("hello");
        """;

    private const string CalculatorDocumentText = """
        public static class Calculator
        {
            public static int Add(int left, int right) => left + right;
        }
        """;

    private const string SolutionText = """
        <Solution>
          <Project Path="App/Fixture.csproj" />
          <Project Path="Tests/Fixture.Tests.csproj" />
        </Solution>
        """;

    private const string FileBasedAppText = """
        #:property TargetFramework=net10.0

        Console.WriteLine("tool");
        """;

    private const string TestDocumentText = """
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Fixture.Tests;

        [TestClass]
        public sealed class ExampleTests
        {
            [TestMethod]
            public void RunsFromVsCode()
            {
                Assert.AreEqual(4, global::Calculator.Add(2, 2));
            }
        }
        """;

    private const string TestProjectText = """
        <Project Sdk="MSTest.Sdk/4.3.3">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../App/Fixture.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string ShutdownTestProjectText = """
        <Project Sdk="MSTest.Sdk/4.3.3">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <Target Name="BlockAutomaticDiscovery" BeforeTargets="Build">
            <Exec Command="/bin/sh &quot;$(MSBuildProjectDirectory)/block-discovery.sh&quot; &quot;$(MSBuildProjectDirectory)/discovery.pid&quot; &quot;$(MSBuildProjectDirectory)/discovery-process-tree.txt&quot;" />
          </Target>
        </Project>
        """;

    private async Task RunVsCodeAsync(
        string repositoryRoot,
        string runnerPath,
        string extensionPath,
        string runtimeExtensionPath,
        string workspacePath,
        string userDataPath,
        string extensionsPath,
        string? remoteServerRoot,
        string remoteDataPath,
        string? displayName,
        string? remoteSuite = null,
        TimeSpan? runTimeout = null,
        bool trackControlSession = true,
        string? localSuite = null)
    {
        string socketDirectory = Path.Join(
            Path.GetDirectoryName(userDataPath)!,
            "control-sockets");
        Directory.CreateDirectory(socketDirectory);
        string? remoteTestExtensionPath = remoteServerRoot is null
            ? null
            : await VsCodeExtensionPackage.GetRemoteTestAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);
        using Process runner = StartRunner(
            repositoryRoot,
            runnerPath,
            extensionPath,
            runtimeExtensionPath,
            workspacePath,
            userDataPath,
            extensionsPath,
            remoteServerRoot,
            remoteDataPath,
            displayName,
            remoteSuite,
            localSuite,
            socketDirectory,
            remoteTestExtensionPath);
        Task<string> outputTask = runner.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = runner.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        ProcessExitObservation? serverExit = null;
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            Task runnerExitTask = runner.WaitForExitAsync(TestContext.CancellationToken);
            if (trackControlSession)
            {
                Task<ControlSessionInfo> sessionTask = ControlSessionWaiter.WaitForRunningAsync(
                    workspacePath,
                    s_editorStartupTimeout,
                    startupCancellation.Token,
                    socketDirectory: socketDirectory);
                Task firstCompleted = await Task.WhenAny(sessionTask, runnerExitTask)
                    .ConfigureAwait(false);
                if (firstCompleted == sessionTask)
                {
                    ControlSessionInfo session = await sessionTask.ConfigureAwait(false);
                    serverExit = ProcessExitWaiter.Observe(session.ProcessId);
                }
            }

            await runnerExitTask
                .WaitAsync(runTimeout ?? TimeSpan.FromMinutes(2), TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await startupCancellation.CancelAsync().ConfigureAwait(false);
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            TestContext.WriteLine(output);
            TestContext.WriteLine(error);
            if (serverExit is ProcessExitObservation observation)
            {
                await ProcessExitWaiter.WaitAsync(
                    observation,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }
        }

        Assert.AreEqual(0, runner.ExitCode);
    }

    private async Task AssertNoUnexpectedCslsOutputAsync(
        IReadOnlyList<string> dataPaths,
        bool expectWorkspaceRestore,
        CancellationToken cancellationToken,
        string? requiredRestoredEntryPointFileName = null)
    {
        string[] logPaths = [.. dataPaths
            .Where(Directory.Exists)
            .SelectMany(dataPath => Directory.GetFiles(
                dataPath,
                "*.log",
                SearchOption.AllDirectories))];
        Assert.IsNotEmpty(logPaths, "VS Code did not persist any logs for error validation.");
        string[] cslsLogPaths = [.. logPaths.Where(logPath =>
            logPath.Contains("willibrandon.csls", StringComparison.OrdinalIgnoreCase))];
        Assert.IsNotEmpty(
            cslsLogPaths,
            $"VS Code did not persist the csls output log. Found:{Environment.NewLine}{string.Join(Environment.NewLine, logPaths)}");

        var cslsFailures = new List<string>();
        var cslsBlankLines = new List<string>();
        var cslsNestedLogLevels = new List<string>();
        var cslsOutputLines = new List<string>();
        var editorFailures = new List<string>();
        foreach (string logPath in logPaths)
        {
            string[] lines = await File.ReadAllLinesAsync(logPath, cancellationToken)
                .ConfigureAwait(false);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (cslsLogPaths.Contains(logPath, StringComparer.Ordinal))
                {
                    cslsOutputLines.Add(lines[lineIndex]);
                }

                if (
                    cslsLogPaths.Contains(logPath, StringComparer.Ordinal) &&
                    lines[lineIndex].Length == 0)
                {
                    cslsBlankLines.Add($"{logPath}:{lineIndex + 1}");
                }

                if (
                    lines[lineIndex].Contains(" [error] ", StringComparison.OrdinalIgnoreCase) ||
                    lines[lineIndex].Contains(" [warning] ", StringComparison.OrdinalIgnoreCase))
                {
                    string failure = $"{logPath}:{lineIndex + 1}: {lines[lineIndex]}";
                    (cslsLogPaths.Contains(logPath, StringComparer.Ordinal)
                        ? cslsFailures
                        : editorFailures).Add(failure);
                }

                if (
                    cslsLogPaths.Contains(logPath, StringComparer.Ordinal) &&
                    s_nestedLogLevelMarkers.Any(marker => lines[lineIndex].Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    cslsNestedLogLevels.Add(
                        $"{logPath}:{lineIndex + 1}: {lines[lineIndex]}");
                }
            }
        }

        if (editorFailures.Count > 0)
        {
            TestContext.WriteLine(
                $"VS Code emitted non-CSLS warning or error log entries:{Environment.NewLine}" +
                string.Join(Environment.NewLine, editorFailures));
        }

        Assert.IsEmpty(
            cslsFailures,
            $"The VS Code CSLS output emitted warning or error log entries:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, cslsFailures)}");
        Assert.IsEmpty(
            cslsBlankLines,
            $"The VS Code CSLS output contained blank physical lines:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, cslsBlankLines)}");
        Assert.IsEmpty(
            cslsNestedLogLevels,
            $"The VS Code CSLS output repeated structured log levels:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, cslsNestedLogLevels)}");
        Assert.DoesNotContain(
            static line => line.Contains(
                "Watched file changes completed",
                StringComparison.Ordinal),
            cslsOutputLines,
            "Ordinary watched-file edits must remain quiet at Information level.");
        Assert.DoesNotContain(
            static line =>
                line.Contains("-getProperty:TargetPath", StringComparison.Ordinal) ||
                line.Contains("--list-tests", StringComparison.Ordinal) ||
                line.Contains("\"displayName\":", StringComparison.Ordinal),
            cslsOutputLines,
            "Automatic test discovery must not write internal commands or JSON to csls output.");
        Assert.Contains(
            static line =>
                line.Contains("Discovered ", StringComparison.Ordinal) &&
                line.Contains(" workspace entry points in ", StringComparison.Ordinal) &&
                line.Contains(" ms", StringComparison.Ordinal),
            cslsOutputLines,
            "The VS Code CSLS output omitted timed workspace discovery progress.");
        if (requiredRestoredEntryPointFileName is not null)
        {
            Assert.Contains(
                line =>
                    line.Contains("Restoring ", StringComparison.Ordinal) &&
                    line.Contains(requiredRestoredEntryPointFileName, StringComparison.Ordinal),
                cslsOutputLines,
                $"The VS Code CSLS output omitted restore progress for " +
                $"{requiredRestoredEntryPointFileName}.");
            Assert.Contains(
                line =>
                    line.Contains("Restored ", StringComparison.Ordinal) &&
                    line.Contains(requiredRestoredEntryPointFileName, StringComparison.Ordinal) &&
                    line.Contains(" in ", StringComparison.Ordinal) &&
                    line.Contains(" ms", StringComparison.Ordinal),
                cslsOutputLines,
                $"The VS Code CSLS output omitted timed restore completion for " +
                $"{requiredRestoredEntryPointFileName}.");
        }
        else if (expectWorkspaceRestore)
        {
            Assert.Contains(
                static line => line.Contains("Restoring ", StringComparison.Ordinal),
                cslsOutputLines,
                "The VS Code CSLS output omitted workspace restore progress.");
            Assert.Contains(
                static line =>
                    line.Contains("Restored ", StringComparison.Ordinal) &&
                    line.Contains(" in ", StringComparison.Ordinal) &&
                    line.Contains(" ms", StringComparison.Ordinal),
                cslsOutputLines,
                "The VS Code CSLS output omitted timed workspace restore completion.");
        }
        else
        {
            Assert.DoesNotContain(
                static line =>
                    line.Contains("Restoring ", StringComparison.Ordinal) ||
                    line.Contains("Restored ", StringComparison.Ordinal),
                cslsOutputLines,
                "Workspace startup must not restore unopened file-based apps.");
        }
        Assert.Contains(
            static line =>
                line.Contains(
                    "Completed initial C# workspace load in ",
                    StringComparison.Ordinal) &&
                line.Contains(" ms with ", StringComparison.Ordinal) &&
                line.Contains(" projects", StringComparison.Ordinal),
            cslsOutputLines,
            "The VS Code CSLS output omitted timed initial workspace load completion.");
        Assert.Contains(
            static line =>
                line.Contains("C# workspace ready in ", StringComparison.Ordinal) &&
                line.Contains(" ms", StringComparison.Ordinal),
            cslsOutputLines,
            "The VS Code CSLS output omitted the total workspace-ready timing.");
    }

    private static Process StartRunner(
        string repositoryRoot,
        string runnerPath,
        string extensionPath,
        string runtimeExtensionPath,
        string workspacePath,
        string userDataPath,
        string extensionsPath,
        string? remoteServerRoot,
        string remoteDataPath,
        string? displayName,
        string? remoteSuite,
        string? localSuite,
        string socketDirectory,
        string? remoteTestExtensionPath)
    {
        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string vscodeCachePath = Path.Join(
            toolsRoot,
            "vscode",
            "stable");
        Directory.CreateDirectory(vscodeCachePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.Environment["CSLS_VSCODE_CACHE_PATH"] = vscodeCachePath;
        startInfo.Environment["CSLS_VSCODE_EXTENSION_PATH"] = extensionPath;
        startInfo.Environment["CSLS_VSCODE_EXTENSIONS_PATH"] = extensionsPath;
        startInfo.Environment["CSLS_VSCODE_EXPECTED_HOST"] = remoteServerRoot is null
            ? "desktop"
            : "remote";
        startInfo.Environment["CSLS_VSCODE_RUNTIME_EXTENSION_PATH"] = runtimeExtensionPath;
        startInfo.Environment["CSLS_VSCODE_USER_DATA_PATH"] = userDataPath;
        startInfo.Environment["CSLS_VSCODE_WORKSPACE_PATH"] = workspacePath;
        startInfo.Environment[ControlEndpoint.SocketDirectoryEnvironmentVariable] =
            socketDirectory;
        if (localSuite is not null)
        {
            startInfo.Environment["CSLS_VSCODE_SUITE"] = localSuite;
        }
        if (remoteServerRoot is not null)
        {
            startInfo.Environment["CSLS_VSCODE_REMOTE_TEST_EXTENSION_PATH"] =
                remoteTestExtensionPath ?? throw new InvalidOperationException(
                    "The remote VS Code test extension is unavailable.");
            startInfo.Environment["CSLS_VSCODE_REMOTE_DATA_PATH"] = remoteDataPath;
            startInfo.Environment["CSLS_VSCODE_REMOTE_RESULT_PATH"] = Path.Join(
                remoteDataPath,
                "test-result.json");
            startInfo.Environment["CSLS_VSCODE_REMOTE_SERVER_ROOT"] = remoteServerRoot;
            if (remoteSuite is not null)
            {
                startInfo.Environment["CSLS_VSCODE_REMOTE_SUITE"] = remoteSuite;
            }
        }
        if (displayName is not null)
        {
            startInfo.Environment["DISPLAY"] = displayName;
            startInfo.Environment.Remove("WAYLAND_DISPLAY");
            startInfo.Environment["XDG_SESSION_TYPE"] = "x11";
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The VS Code integration runner did not start.");
    }

    /// <summary>
    /// Returns the requested process while treating an already-exited process as absent.
    /// </summary>
    private static Process? TryGetProcessById(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string CreateSettingsText(string debuggerPath) => $$"""
        {
          "chat.disableAIFeatures": true,
          "csls.debugger.path": {{JsonSerializer.Serialize(debuggerPath)}},
          "csls.diagnostics.reportInformationAsHint": false,
          "telemetry.telemetryLevel": "off",
          "workbench.enableExperiments": false,
          "workbench.colorTheme": "csls Theme Without Semantic Highlighting",
          "workbench.startupEditor": "none"
        }
        """;
}
