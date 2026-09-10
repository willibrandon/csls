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
    /// Preserves caller cancellation before starting another owned child process.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledCaptureDoesNotStartProcess()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await operation.CancelAsync().ConfigureAwait(false);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--debugger-process-tree-child");
        start.ArgumentList.Add("leaf");
        int? startedProcessId = null;

        OperationCanceledException canceled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await DebuggerTestProcess.RunAsync(start, operation.Token,
                observeProcess: (process, _, _) =>
                {
                    startedProcessId = process.Id;
                    return Task.CompletedTask;
                }).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(operation.Token, canceled.CancellationToken);
        Assert.IsNull(startedProcessId, "A canceled capture must not start another child process.");
    }

    /// <summary>
    /// Propagates synchronous and asynchronous observer failures after reaping the independently retained child.
    /// </summary>
    /// <param name="asynchronous">Whether the observer fails after yielding its execution.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ObserverFailureReapsRunningProcess(bool asynchronous)
    {
        using var children = new DisposableCollection<Process>();
        Process? retained = null;
        var failure = new IOException("The process observation sink rejected the owned child.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--debugger-process-tree-child");
        start.ArgumentList.Add("leaf");
        IOException observed = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await DebuggerTestProcess.RunAsync(start, TestContext.CancellationToken,
                observeProcess: (process, report, token) =>
                {
                    _ = report;
                    token.ThrowIfCancellationRequested();
                    retained = children.Acquire(() => Process.GetProcessById(process.Id));
                    _ = retained.SafeHandle;
                    return asynchronous ? FailAsync() : throw failure;
                }).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreSame(failure, observed);
        Assert.IsNotNull(retained);
        Assert.IsTrue(retained.HasExited);
        Assert.IsTrue(await Task.Run(() => retained.WaitForExit(0), TestContext.CancellationToken).ConfigureAwait(false),
            "The observer failure must reach its caller after the owned process object is signaled.");

        async Task FailAsync()
        {
            await Task.Yield();
            throw failure;
        }
    }

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
    /// <param name="rejectObservation">Whether the drain observer rejects capture after the root exits.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InheritedOutputCaptureHonorsCompletionAndCancellation(bool reportProgress, bool cancelCapture,
        bool rejectObservation)
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
        var draining = new TaskCompletionSource<(int ProcessId, bool HasExited, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observerFailure = new IOException("The output-drain observer rejected capture.");
        int drainObservations = 0;
        int observerFinished = 0;
        Task<(int ProcessId, int ExitCode, string Output, string Error)> running = DebuggerTestProcess.RunWithIdentityAsync(
            start, operation.Token, reportProgress ? progress.Enqueue : null,
            observeOutputDrain: async (process, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref drainObservations);
                draining.SetResult((process.Id, process.HasExited, token));
                if (rejectObservation)
                {
                    throw observerFailure;
                }
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = token.Register(() => stopped.SetResult());
                await stopped.Task.ConfigureAwait(false);
                Volatile.Write(ref observerFinished, 1);
            });
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
            Assert.IsFalse(draining.Task.IsCompleted, "The root has not been released to exit.");
            await rootPipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            await root.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, root.ExitCode);
            Assert.IsFalse(child.HasExited);
            (int observedId, bool observedExit, CancellationToken observerToken) = await draining.Task
                .WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(root.Id, observedId);
            Assert.IsTrue(observedExit, "Output-drain observation requires an exited process.");
            Assert.AreEqual(1, Volatile.Read(ref drainObservations));
            if (rejectObservation)
            {
                IOException failure = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await running.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreSame(observerFailure, failure);
                Assert.IsFalse(child.HasExited, "Observer failure must preserve the independently transferred child.");
            }
            else
            {
                Assert.IsFalse(running.IsCompleted, "The live descendant still owns both redirected write handles.");
            }

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
            if (!cancelCapture && !rejectObservation)
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
            Assert.AreEqual(1, Volatile.Read(ref drainObservations), "Each exited process must be observed once.");
            Assert.IsTrue(observerToken.IsCancellationRequested,
                "Completed capture must stop its output-drain observer before returning to the caller.");
            Assert.AreEqual(rejectObservation ? 0 : 1, Volatile.Read(ref observerFinished),
                "Capture must await observer cleanup before returning its result or cancellation.");
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

    /// <summary>
    /// Completes an unredirected capture when an independent descendant retains inherited standard streams.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnredirectedCaptureDoesNotWaitForDescendantStreamOwnership()
    {
        string rootPipeName = $"csls-{Guid.NewGuid():N}";
        string childPipeName = $"csls-{Guid.NewGuid():N}";
        using var rootPipe = new NamedPipeServerStream(rootPipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var childPipe = new NamedPipeServerStream(childPipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rootConnected = rootPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        Task childConnected = childPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--debugger-inherited-output-root");
        start.ArgumentList.Add(rootPipeName);
        start.ArgumentList.Add(childPipeName);
        Task<(int ProcessId, int ExitCode, string Output, string Error)> running =
            DebuggerTestProcess.RunWithIdentityAsync(start, TestContext.CancellationToken,
                redirectStandardStreams: false);
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
            (int processId, int exitCode, string output, string error) = await running
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(root.Id, processId);
            Assert.AreEqual(0, exitCode);
            Assert.AreEqual(string.Empty, output);
            Assert.AreEqual(string.Empty, error);
            Assert.IsFalse(child.HasExited,
                "An independently transferred descendant must remain alive after its unredirected parent exits.");

            await childPipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, child.ExitCode);
        }
        finally
        {
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
