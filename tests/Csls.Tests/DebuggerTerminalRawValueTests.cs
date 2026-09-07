using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Layout;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies terminal launch and attach presentation through real PTY processes and screen snapshots.
/// </summary>
[TestClass]
public sealed class DebuggerTerminalRawValueTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token and snapshot output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Renders the selected raw-value policy in a watch while preserving launch and attach ownership.
    /// </summary>
    /// <param name="raw">Whether the CLI selects physical runtime presentation.</param>
    /// <param name="attach">Whether the terminal attaches to an independently started target.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [TestCategory("DebuggerTerminal")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TerminalRawValueOptionControlsWatchPresentation(bool raw, bool attach)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-terminal-raw-values-");
        try
        {
            await ExerciseTerminalAsync(directory.FullName, raw, attach).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task ExerciseTerminalAsync(string directory, bool raw, bool attach)
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string artifacts = EditorToolResolver.ResolveArtifactsRoot(repository);
        string program = EditorToolResolver.ResolveTestProcessHost(repository);
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        string signal = Path.Join(directory, "continue.signal");
        int line = (await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(announcement);", StringComparison.Ordinal)).Line;
        using Process? target = attach ? StartTarget(program, signal, directory) : null;
        try
        {
            if (target is not null)
            {
                char[] ready = new char[5];
                Assert.AreEqual(ready.Length,
                    await target.StandardOutput.ReadBlockAsync(ready, TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual("ready", new string(ready));
            }
            string[] activation = target is null
                ? ["launch", program, "--source", source, "--line", line.ToString(CultureInfo.InvariantCulture)]
                : ["attach", target.Id.ToString(CultureInfo.InvariantCulture)];
            string[] arguments = target is null ? ["--", "--debugger-fixture", signal] : [];
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_DEBUGGER_WORKER_PATH"] = Path.Join(artifacts, "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll"),
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int Width = 180;
            const int Height = 35;
            var workload = new Hex1bPtyWorkload(EditorToolResolver.ResolveDotNetHost(),
                [EditorToolResolver.ResolveLauncher(repository), "debugger", "tui", .. activation,
                    "--show-raw-values", raw ? "true" : "false", "--source-file-map", $"/_/={repository}", .. arguments],
                directory, Width, Height, environment);
            await using ConfiguredAsyncDisposable cleanup = workload.ConfigureAwait(false);
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload).WithHeadless().WithDimensions(Width, Height).Build();
            var watches = new Rect(66, Height - 7, Width - 66, 4);
            int exitCode = await workload.RunAsync(terminal, async () =>
            {
                var automator = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
                await automator.WaitUntilAsync(screen => screen.InAlternateScreen && screen.GetLine(0).Contains("Stopped", StringComparison.Ordinal),
                    description: "stopped target in the debugger terminal").ConfigureAwait(false);
                await automator.WaitUntilTextAsync("WaitForSignal").ConfigureAwait(false);
                await automator.KeyAsync(Hex1bKey.F1, TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Debugger commands").ConfigureAwait(false);
                await automator.TypeAsync("add watch", TestContext.CancellationToken).ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Watch expression").ConfigureAwait(false);
                await automator.TypeAsync("localDisplay", TestContext.CancellationToken).ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                string expected = raw ? "localDisplay = {Csls.TestProcessHost.DebuggerDisplayFixture}"
                    : "localDisplay = {id}=54; label=alpha\\nbeta; nested=55";
                await automator.WaitUntilAsync(screen => screen.GetRegion(watches).ContainsText(expected),
                    description: "watch value using the selected presentation policy").ConfigureAwait(false);
                using (Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot())
                {
                    Assert.Contains(expected, snapshot.GetRegion(watches).GetText());
                    Assert.Contains("Stopped", snapshot.GetLine(0));
                    TestContext.WriteLine(snapshot.GetScreenText());
                }
                await automator.Ctrl().KeyAsync(Hex1bKey.C, TestContext.CancellationToken).ConfigureAwait(false);
            }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode);
            if (target is not null)
            {
                Assert.IsFalse(target.HasExited);
                await File.WriteAllTextAsync(signal, string.Empty, TestContext.CancellationToken).ConfigureAwait(false);
                await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, target.ExitCode);
            }
        }
        finally
        {
            if (target is not null && !target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static Process StartTarget(string program, string signal, string directory)
    {
        var startInfo = new ProcessStartInfo(EditorToolResolver.ResolveDotNetHost())
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(program);
        startInfo.ArgumentList.Add("--debugger-fixture");
        startInfo.ArgumentList.Add(signal);
        return Process.Start(startInfo) ?? throw new AssertFailedException("The independent debugger target did not start.");
    }
}
