using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies the interactive debugger through a real launcher, worker, and PTY.
/// </summary>
[TestClass]
public sealed class DebuggerTerminalTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Navigates real debugger state and keeps execution controls responsive.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TerminalNavigatesStoppedStateAndKeepsExecutionControlsResponsive()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string debuggerWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Debugger.Worker",
            "debug",
            "csls-debugger-worker.dll");
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-tui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            string signalPath = Path.Join(testDirectory, "continue.signal");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_DEBUGGER_WORKER_PATH"] = debuggerWorkerPath,
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int width = 140;
            const int height = 35;
            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                [
                    launcherPath,
                    "debugger",
                    "tui",
                    "launch",
                    EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
                    "--source",
                    sourcePath,
                    "--line",
                    breakpointLine.ToString(CultureInfo.InvariantCulture),
                    "--source-file-map",
                    $"/_/={repositoryRoot}",
                    "--",
                    "--debugger-fixture",
                    signalPath
                ],
                testDirectory,
                width,
                height,
                environment);
            await using ConfiguredAsyncDisposable workloadCleanup = workload.ConfigureAwait(false);
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(width, height)
                .Build();

            int exitCode = await workload.RunAsync(
                terminal,
                async () =>
                {
                    var automator = new Hex1bTerminalAutomator(
                        terminal,
                        defaultTimeout: TimeSpan.FromSeconds(30));
                    await automator.WaitUntilAsync(
                        screen => screen.InAlternateScreen ||
                            screen.ContainsText("Unhandled exception"),
                        description: "debugger terminal startup")
                        .ConfigureAwait(false);
                    using (Hex1bTerminalSnapshot startup = automator.CreateSnapshot())
                    {
                        Assert.IsTrue(startup.InAlternateScreen, startup.GetScreenText());
                    }

                    await automator.WaitUntilTextAsync("csls debugger").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Source").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Threads").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Stack").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Arguments and Locals").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Target Output").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("WaitForSignal").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("with symbols").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("number = 42").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("localNumber = 0").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync($"●    {breakpointLine}")
                        .ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F2,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Modules").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("csls-test-process-host")
                        .ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F2,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Breakpoints").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync(
                        $"{Path.GetFileName(sourcePath)}:{breakpointLine}")
                        .ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F2,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("No current managed exception.")
                        .ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F2,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Target Output").ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F9,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync(
                        $"Removed breakpoint at {Path.GetFileName(sourcePath)}:{breakpointLine}.")
                        .ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F10,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("localNumber = 43").ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F5,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Target is running.").ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F6,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("out> ready").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("localNumber = 43").ConfigureAwait(false);
                    await automator.Ctrl().KeyAsync(
                        Hex1bKey.C,
                        TestContext.CancellationToken).ConfigureAwait(false);
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
