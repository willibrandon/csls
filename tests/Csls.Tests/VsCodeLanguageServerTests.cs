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

    private async Task RunVsCodeHostAsync(bool remote)
    {
        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
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
            "3.1.0",
            platformSpecific: false);
        string? remoteServerRoot = remote
            ? EditorToolResolver.ResolveVsCodeRemoteServerRoot(repositoryRoot)
            : null;

        string fixturePath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "v",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string userDataPath = Path.Join(fixturePath, "u");
            string extensionsPath = Path.Join(fixturePath, "extensions");
            string remoteDataPath = Path.Join(fixturePath, "remote");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(userDataPath);
            Directory.CreateDirectory(extensionsPath);
            Directory.CreateDirectory(remoteDataPath);
            string settingsPath = Path.Join(userDataPath, "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllTextAsync(
                settingsPath,
                CreateSettingsText(launcherPath),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                DocumentText,
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
                    launcherPath,
                    workerPath,
                    extensionPath,
                    runtimeExtensionPath,
                    workspacePath,
                    userDataPath,
                    extensionsPath,
                    remoteServerRoot,
                    remoteDataPath,
                    display.DisplayName).ConfigureAwait(false);
            }
            else
            {
                await RunVsCodeAsync(
                    repositoryRoot,
                    runnerPath,
                    launcherPath,
                    workerPath,
                    extensionPath,
                    runtimeExtensionPath,
                    workspacePath,
                    userDataPath,
                    extensionsPath,
                    remoteServerRoot,
                    remoteDataPath,
                    displayName: null).ConfigureAwait(false);
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

    private const string DocumentText = """
        Console.WriteLine("hello");
        """;

    private async Task RunVsCodeAsync(
        string repositoryRoot,
        string runnerPath,
        string launcherPath,
        string workerPath,
        string extensionPath,
        string runtimeExtensionPath,
        string workspacePath,
        string userDataPath,
        string extensionsPath,
        string? remoteServerRoot,
        string remoteDataPath,
        string? displayName)
    {
        using Process runner = StartRunner(
            repositoryRoot,
            runnerPath,
            launcherPath,
            workerPath,
            extensionPath,
            runtimeExtensionPath,
            workspacePath,
            userDataPath,
            extensionsPath,
            remoteServerRoot,
            remoteDataPath,
            displayName);
        Task<string> outputTask = runner.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = runner.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        int? serverProcessId = null;
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            Task<ControlSessionInfo> sessionTask = ControlSessionWaiter.WaitForRunningAsync(
                workspacePath,
                s_editorStartupTimeout,
                startupCancellation.Token);
            Task runnerExitTask = runner.WaitForExitAsync(TestContext.CancellationToken);
            Task firstCompleted = await Task.WhenAny(sessionTask, runnerExitTask)
                .ConfigureAwait(false);
            if (firstCompleted == sessionTask)
            {
                ControlSessionInfo session = await sessionTask.ConfigureAwait(false);
                serverProcessId = session.ProcessId;
            }

            await runnerExitTask
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.CancellationToken)
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
            if (serverProcessId is int processId)
            {
                await ProcessExitWaiter.WaitAsync(
                    processId,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }
        }

        Assert.AreEqual(0, runner.ExitCode);
    }

    private static Process StartRunner(
        string repositoryRoot,
        string runnerPath,
        string launcherPath,
        string workerPath,
        string extensionPath,
        string runtimeExtensionPath,
        string workspacePath,
        string userDataPath,
        string extensionsPath,
        string? remoteServerRoot,
        string remoteDataPath,
        string? displayName)
    {
        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string vscodeCachePath = Path.Join(
            toolsRoot,
            "vscode",
            "1.135.0");
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
        startInfo.Environment["CSLS_VSCODE_EXPECTED_SERVER_PATH"] = launcherPath;
        startInfo.Environment["CSLS_VSCODE_EXTENSION_PATH"] = extensionPath;
        startInfo.Environment["CSLS_VSCODE_EXTENSIONS_PATH"] = extensionsPath;
        startInfo.Environment["CSLS_VSCODE_EXPECTED_HOST"] = remoteServerRoot is null
            ? "desktop"
            : "remote";
        startInfo.Environment["CSLS_VSCODE_RUNTIME_EXTENSION_PATH"] = runtimeExtensionPath;
        startInfo.Environment["CSLS_VSCODE_USER_DATA_PATH"] = userDataPath;
        startInfo.Environment["CSLS_VSCODE_WORKSPACE_PATH"] = workspacePath;
        startInfo.Environment["CSLS_WORKER_PATH"] = workerPath;
        if (remoteServerRoot is not null)
        {
            startInfo.Environment["CSLS_VSCODE_REMOTE_DATA_PATH"] = remoteDataPath;
            startInfo.Environment["CSLS_VSCODE_REMOTE_RESULT_PATH"] = Path.Join(
                remoteDataPath,
                "test-result.json");
            startInfo.Environment["CSLS_VSCODE_REMOTE_SERVER_ROOT"] = remoteServerRoot;
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

    private static string CreateSettingsText(string launcherPath) => $$"""
        {
          "chat.disableAIFeatures": true,
          "csls.server.path": {{JsonSerializer.Serialize(launcherPath)}},
          "telemetry.telemetryLevel": "off",
          "workbench.enableExperiments": false,
          "workbench.startupEditor": "none"
        }
        """;
}
