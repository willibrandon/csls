using Csls.Debugger.Terminal;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace Csls.Tests;

/// <summary>
/// Verifies debugger presentation notifications through real Hex1b queues and rendered screens.
/// </summary>
[TestClass]
[TestCategory("DebuggerTerminal")]
public sealed class DebuggerTerminalRefreshTests
{
    /// <summary>
    /// Gets or sets the current test's cancellation and diagnostic context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves publications made while an older frame is rendering and rearms the next update.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PublicationDuringRenderReachesScreenWithoutFurtherInput()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-terminal-refresh-");
        try
        {
            string statePath = Path.Join(directory.FullName, "state.txt");
            await File.WriteAllTextAsync(
                statePath,
                "Initial debugger view",
                TestContext.CancellationToken).ConfigureAwait(false);
            var capturedOldFrame = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOldFrame = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            DebuggerTerminalRefresh? refresh = null;
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithHex1bApp(
                    options => refresh = new DebuggerTerminalRefresh(
                        Assert.IsInstanceOfType<Hex1bAppWorkloadAdapter>(options.WorkloadAdapter)),
                    async context =>
                    {
                        Assert.IsNotNull(refresh);
                        refresh.Acknowledge();
                        string text = await File.ReadAllTextAsync(
                            statePath,
                            cancellation.Token).ConfigureAwait(false);
                        if (text == "Older debugger view")
                        {
                            capturedOldFrame.TrySetResult(text);
                            await releaseOldFrame.Task.WaitAsync(cancellation.Token)
                                .ConfigureAwait(false);
                        }

                        return context.Text(text);
                    })
                .WithHeadless()
                .WithDimensions(60, 6)
                .Build();
            Assert.IsNotNull(refresh);
            Task<int> runTask = terminal.RunAsync(cancellation.Token);
            try
            {
                var automator = new Hex1bTerminalAutomator(
                    terminal,
                    defaultTimeout: TimeSpan.FromSeconds(10));
                await automator.WaitUntilTextAsync("Initial debugger view").ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    statePath,
                    "Older debugger view",
                    TestContext.CancellationToken).ConfigureAwait(false);
                refresh.Request();
                Assert.AreEqual(
                    "Older debugger view",
                    await capturedOldFrame.Task.WaitAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false));

                await File.WriteAllTextAsync(
                    statePath,
                    "Complete locals: localNumber = 43",
                    TestContext.CancellationToken).ConfigureAwait(false);
                refresh.Request();
                releaseOldFrame.SetResult();
                await automator.WaitUntilTextAsync("Complete locals: localNumber = 43")
                    .ConfigureAwait(false);
                using (Hex1bTerminalSnapshot screen = automator.CreateSnapshot())
                {
                    Assert.DoesNotContain("Older debugger view", screen.GetScreenText());
                }

                await File.WriteAllTextAsync(
                    statePath,
                    "Next stop: localNumber = 44",
                    TestContext.CancellationToken).ConfigureAwait(false);
                refresh.Request();
                await automator.WaitUntilTextAsync("Next stop: localNumber = 44")
                    .ConfigureAwait(false);
                using Hex1bTerminalSnapshot nextScreen = automator.CreateSnapshot();
                Assert.DoesNotContain("localNumber = 43", nextScreen.GetScreenText());
            }
            finally
            {
                releaseOldFrame.TrySetResult();
                await cancellation.CancelAsync().ConfigureAwait(false);
                Assert.AreEqual(0, await runTask.ConfigureAwait(false));
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                directory.FullName,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues one typed notification per unobserved publication batch and rearms after capture.
    /// </summary>
    [TestMethod]
    public void RequestsCoalesceUntilAcknowledged()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(60, 6)
            .Build();
        var refresh = new DebuggerTerminalRefresh(workload);
        for (int index = 0; index < 100; index++)
        {
            refresh.Request();
        }

        Assert.IsTrue(workload.InputEvents.TryRead(out Hex1bEvent? first));
        Assert.IsInstanceOfType<DebuggerTerminalRefreshEvent>(first);
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
        refresh.Request();
        Assert.IsFalse(workload.InputEvents.TryRead(out _));

        refresh.Acknowledge();
        refresh.Request();
        Assert.IsTrue(workload.InputEvents.TryRead(out Hex1bEvent? next));
        Assert.IsInstanceOfType<DebuggerTerminalRefreshEvent>(next);
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
    }

    /// <summary>
    /// Accepts late publications after terminal shutdown without reopening its completed queue.
    /// </summary>
    [TestMethod]
    public void LatePublicationsAfterTerminalShutdownDoNotQueueEvents()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        var refresh = new DebuggerTerminalRefresh(workload);
        using (Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(60, 6)
            .Build())
        {
            Assert.IsFalse(workload.InputEvents.TryRead(out _));
        }

        refresh.Request();
        refresh.Acknowledge();
        refresh.Request();
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
        Assert.IsTrue(workload.InputEvents.Completion.IsCompletedSuccessfully);
    }
}
