using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies process capture cancellation while independently owned descendants retain inherited output streams.
/// </summary>
[TestClass]
public sealed class DebuggerTestProcessTests : DapTestContext
{
    /// <summary>
    /// Propagates an output sink failure while reaping the child that is still waiting for input.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task OutputFailureReapsRunningProcess()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var children = new DisposableCollection<Process>();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--debugger-process-tree-child");
        start.ArgumentList.Add("leaf");
        var failure = new IOException("The output sink rejected the running child's announcement.");
        Process? retained = null;
        Task running = DebuggerTestProcess.RunWithIdentityAsync(start, operation.Token, line =>
        {
            retained = children.Acquire(() => Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture)));
            _ = retained.SafeHandle;
            throw failure;
        });
        try
        {
            IOException observed = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.AreSame(failure, observed);
            Assert.IsFalse(operation.IsCancellationRequested);
            Assert.IsNotNull(retained);
            Assert.IsTrue(await Task.Run(() => retained.WaitForExit(0), TestContext.CancellationToken).ConfigureAwait(false),
                "The owned process must be reaped before its output failure reaches the caller.");
        }
        finally
        {
            await operation.CancelAsync().ConfigureAwait(false);
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Completes or cancels inherited output capture after parent exit while preserving independent child ownership.
    /// </summary>
    /// <param name="reportProgress">Whether output is captured through the incremental progress path.</param>
    /// <param name="cancelCapture">Whether to cancel capture before releasing the inherited write handles.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InheritedOutputCaptureHonorsCompletionAndCancellation(bool reportProgress, bool cancelCapture)
    {
        string rootPipeName = $"csls-{Guid.NewGuid():N}";
        string childPipeName = $"csls-{Guid.NewGuid():N}";
        using var rootPipe = new NamedPipeServerStream(rootPipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var childPipe = new NamedPipeServerStream(childPipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rootConnected = rootPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        Task childConnected = childPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--debugger-inherited-output-root");
        start.ArgumentList.Add(rootPipeName);
        start.ArgumentList.Add(childPipeName);
        var progress = new ConcurrentQueue<string>();
        Task<(int ProcessId, int ExitCode, string Output, string Error)> running = DebuggerTestProcess.RunWithIdentityAsync(
            start, operation.Token, reportProgress ? progress.Enqueue : null);
        Process? root = null;
        Process? child = null;
        try
        {
            await rootConnected.ConfigureAwait(false);
            using var identities = new StreamReader(rootPipe, leaveOpen: true);
            string? announcement = await identities.ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(announcement);
            int[] ids = [.. announcement.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
            Assert.HasCount(2, ids);
            root = Process.GetProcessById(ids[0]);
            child = Process.GetProcessById(ids[1]);
            _ = root.SafeHandle;
            _ = child.SafeHandle;
            await childConnected.ConfigureAwait(false);
            await rootPipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            await root.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, root.ExitCode);
            Assert.IsFalse(child.HasExited);
            Assert.IsFalse(running.IsCompleted, "The live descendant still owns both redirected write handles.");

            if (cancelCapture)
            {
                await operation.CancelAsync().ConfigureAwait(false);
                OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
                Assert.AreEqual(operation.Token, canceled.CancellationToken);
                Assert.IsTrue(running.IsCompleted);
                Assert.IsFalse(child.HasExited, "Canceling capture must preserve the independently transferred child.");
            }
            await childPipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, child.ExitCode);
            if (!cancelCapture)
            {
                (int processId, int exitCode, string output, string error) = await running
                    .WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(root.Id, processId);
                Assert.AreEqual(0, exitCode);
                string[] expected = [rootPipeName, childPipeName];
                Assert.AreSequenceEqual(expected.Order(),
                    output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order());
                Assert.AreSequenceEqual(expected.Order(),
                    error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order());
                Assert.AreSequenceEqual(reportProgress ? expected.Concat(expected).Order() : [], progress.Order());
            }
        }
        finally
        {
            await operation.CancelAsync().ConfigureAwait(false);
            foreach (Process? process in new[] { child, root })
            {
                if (process is null)
                {
                    continue;
                }
                using (process)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            Task completion = running;
            await completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
