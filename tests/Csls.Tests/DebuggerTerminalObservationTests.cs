using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies rejected execution commands preserve live terminal observation.
/// </summary>
[TestClass]
public sealed class DebuggerTerminalObservationTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Renders natural target termination after rejecting a stopped-only execution command.
    /// </summary>
    [TestMethod]
    [DataRow(Hex1bKey.F5, "continue")]
    [DataRow(Hex1bKey.F10, "step")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [TestCategory("DebuggerTerminal")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RejectedExecutionCommandKeepsRunningTargetObservation(
        Hex1bKey key,
        string operation)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
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
            $"csls-debugger-tui-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            string signalPath = Path.Join(testDirectory, "continue.signal");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_DEBUGGER_WORKER_PATH"] = Path.Join(
                    artifactsRoot,
                    "bin",
                    "Csls.Debugger.Worker",
                    "debug",
                    "csls-debugger-worker.dll"),
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int width = 160;
            const int height = 35;
            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                [
                    EditorToolResolver.ResolveLauncher(repositoryRoot),
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
                    await automator.WaitUntilTextAsync("localNumber = 0").ConfigureAwait(false);
                    await automator.KeyAsync(
                        Hex1bKey.F5,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Target is running.").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("out> ready").ConfigureAwait(false);

                    await automator.KeyAsync(key, TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync($"Cannot {operation} while the target is Running.")
                        .ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        signalPath,
                        string.Empty,
                        TestContext.CancellationToken).ConfigureAwait(false);

                    await automator.WaitUntilTextAsync("Target has terminated.").ConfigureAwait(false);
                    using (Hex1bTerminalSnapshot terminated = automator.CreateSnapshot())
                    {
                        Assert.Contains("Terminated", terminated.GetLine(0));
                        Assert.IsFalse(terminated.ContainsText("Target is running."));
                    }

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
