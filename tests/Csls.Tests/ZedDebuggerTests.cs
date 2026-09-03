using Csls.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the bundled debugger through a real Zed process and extension.
/// </summary>
[TestClass]
public sealed class ZedDebuggerTests
{
    private static readonly string[] s_debuggerArguments = ["debugger", "dap"];
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts the csls adapter from Zed and stops on a real source breakpoint.
    /// </summary>
    [TestMethod]
    [TestCategory("ZedHost")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ZedStopsAtSourceBreakpointThroughCslsDebugger()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string fixtureProject = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.CSharp",
            "Csls.Debugger.Fixtures.CSharp.csproj");
        await BuildFixtureAsync(fixtureProject).ConfigureAwait(false);
        string launcherPath = Path.Join(
            Path.GetDirectoryName(EditorToolResolver.ResolveLauncher(repositoryRoot))!,
            "csls");
        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-zed-dap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.GetDirectoryName(fixtureProject)!;
            string userDataPath = Path.Join(fixturePath, "zed-data");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            string socketDirectory = Path.Join(fixturePath, "control-sockets");
            Directory.CreateDirectory(Path.Join(userDataPath, "config"));
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            CopyDirectory(
                EditorToolResolver.ResolveCslsZedExtension(repositoryRoot),
                Path.Join(userDataPath, "extensions", "installed", "csls"));

            string sourcePath = Path.Join(Path.GetDirectoryName(fixtureProject)!, "Program.cs");
            int breakpointLine = FindBreakpointLine(sourcePath);
            string programPath = Path.Join(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Debugger.Fixtures.CSharp",
                "debug",
                "Csls.Debugger.Fixtures.CSharp.dll");
            string signalPath = Path.Join(fixturePath, "target.signal");
            string startedPath = Path.Join(fixturePath, "target.started");
            string continuedPath = Path.Join(fixturePath, "target.continued");
            await WriteDebugConfigurationAsync(
                Path.Join(userDataPath, "config", "debug.json"),
                programPath,
                signalPath,
                startedPath,
                continuedPath).ConfigureAwait(false);
            await WriteSettingsAsync(
                Path.Join(userDataPath, "config", "settings.json"),
                launcherPath).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup = display.ConfigureAwait(false);
            using Process zed = StartZed(
                EditorToolResolver.ResolveZed(repositoryRoot),
                sourcePath,
                breakpointLine,
                workspacePath,
                userDataPath,
                homePath,
                cachePath,
                socketDirectory,
                display.DisplayName,
                EditorToolResolver.ResolveServerWorker(repositoryRoot),
                EditorToolResolver.ResolveDebuggerWorker(repositoryRoot));
            Task<string> output = zed.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
            Task<string> error = zed.StandardError.ReadToEndAsync(TestContext.CancellationToken);
            try
            {
                await FocusZedAsync(display.DisplayName).ConfigureAwait(false);
                string logPath = Path.Join(userDataPath, "logs", "Zed.log");
                await WaitForWorkspaceAsync(logPath).ConfigureAwait(false);
                X11Input.SendF9(display.DisplayName);
                X11Input.SendF4(display.DisplayName);
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    TestContext.CancellationToken).ConfigureAwait(false);
                X11Input.SendEnter(display.DisplayName);
                await WaitForFileAsync(startedPath).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(
                    File.Exists(continuedPath),
                    "The debuggee ran past the requested Zed source breakpoint.");
                X11Input.SendShiftF5(display.DisplayName);
                X11Input.SendControlCharacter(display.DisplayName, 'q');
                await zed.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, zed.ExitCode);
            }
            finally
            {
                if (!zed.HasExited)
                {
                    zed.Kill(entireProcessTree: true);
                    await zed.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                string diagnostics = string.Concat(
                    await output.ConfigureAwait(false),
                    await error.ConfigureAwait(false));
                Assert.DoesNotContain("failed to spawn", diagnostics, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task BuildFixtureAsync(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debugger fixture build did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            string.Concat(await output.ConfigureAwait(false), await error.ConfigureAwait(false)));
    }

    private async Task FocusZedAsync(string displayName)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (X11Input.TryFocusWindow(displayName, "Program.cs"))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        Assert.Fail("Zed did not open the debugger fixture source window.");
    }

    private async Task WaitForFileAsync(string path)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Zed did not launch the debuggee: {path}");
    }

    private async Task WaitForWorkspaceAsync(string logPath)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(logPath))
            {
                string log = await File.ReadAllTextAsync(
                    logPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                if (log.Contains(
                    "starting language server process",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        Assert.Fail("Zed did not finish loading the C# workspace.");
    }

    private static int FindBreakpointLine(string sourcePath) => File
        .ReadAllLines(sourcePath)
        .Select(static (line, index) => (Line: line, Number: index + 1))
        .Single(static candidate => candidate.Line.Contains("answer++;", StringComparison.Ordinal))
        .Number;

    private async Task WriteDebugConfigurationAsync(
        string path,
        string programPath,
        string signalPath,
        string startedPath,
        string continuedPath)
    {
        string json = JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    adapter = "csls",
                    args = new[]
                    {
                        signalPath,
                        "41",
                        "ready",
                        startedPath,
                        continuedPath
                    },
                    cwd = Path.GetDirectoryName(programPath),
                    label = ".NET Launch",
                    program = programPath,
                    request = "launch"
                }
            },
            s_jsonOptions);
        await File.WriteAllTextAsync(path, json, TestContext.CancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteSettingsAsync(string path, string launcherPath)
    {
        string json = JsonSerializer.Serialize(
            new
            {
                auto_update = false,
                dap = new
                {
                    csls = new
                    {
                        args = s_debuggerArguments,
                        binary = launcherPath
                    }
                },
                debugger = new { log_dap_communications = true },
                session = new { trust_all_worktrees = true },
                telemetry = new { diagnostics = false, metrics = false }
            },
            s_jsonOptions);
        await File.WriteAllTextAsync(path, json, TestContext.CancellationToken)
            .ConfigureAwait(false);
    }

    private static Process StartZed(
        string zedPath,
        string sourcePath,
        int line,
        string workspacePath,
        string userDataPath,
        string homePath,
        string cachePath,
        string socketDirectory,
        string displayName,
        string serverWorkerPath,
        string debuggerWorkerPath)
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
        startInfo.ArgumentList.Add($"{sourcePath}:{line}:1");
        startInfo.Environment["DISPLAY"] = displayName;
        startInfo.Environment["CSLS_WORKER_PATH"] = serverWorkerPath;
        startInfo.Environment["CSLS_DEBUGGER_WORKER_PATH"] = debuggerWorkerPath;
        startInfo.Environment[ControlEndpoint.SocketDirectoryEnvironmentVariable] = socketDirectory;
        startInfo.Environment["HOME"] = homePath;
        startInfo.Environment["NO_AT_BRIDGE"] = "1";
        startInfo.Environment["ZED_ALLOW_EMULATED_GPU"] = "1";
        startInfo.Environment.Remove("WAYLAND_DISPLAY");
        startInfo.Environment["XDG_CACHE_HOME"] = cachePath;
        startInfo.Environment["XDG_SESSION_TYPE"] = "x11";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Zed did not start.");
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            string destinationFile = Path.Join(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }
}
