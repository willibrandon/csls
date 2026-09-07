using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies process completion and independent cancellation through real operating-system children.
/// </summary>
[TestClass]
public sealed class DebuggerProcessExitTests : DapTestContext
{
    /// <summary>
    /// Observes native termination for both natural and forced exits, including repeated completed waits.
    /// </summary>
    /// <param name="terminate">Whether to terminate the owned process instead of closing its input.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ProcessCompletionSignalsKernelTermination(bool terminate)
    {
        using Process process = StartInputTarget();
        await VerifyProcessCompletionAsync(process, terminate).ConfigureAwait(false);
    }

    private async Task VerifyProcessCompletionAsync(Process process, bool terminate)
    {
        Task completion = Task.CompletedTask;
        try
        {
            completion = DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken);
            Assert.IsFalse(completion.IsCompleted, "The target remains blocked until its input closes or it is terminated.");
            if (terminate)
            {
                process.Kill(entireProcessTree: true);
            }
            else
            {
                await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            }

            await completion.ConfigureAwait(false);
            AssertKernelTermination(process);
            if (terminate)
            {
                Assert.AreNotEqual(0, process.ExitCode);
            }
            else
            {
                Assert.AreEqual(0, process.ExitCode);
            }

            await DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken).ConfigureAwait(false);
            AssertKernelTermination(process);
            Assert.AreEqual(string.Empty,
                await process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty,
                await process.StandardError.ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await ReleaseTargetAsync(process).ConfigureAwait(false);
            await completion.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels one observer without completing another observer or terminating their target.
    /// </summary>
    /// <param name="cancelBeforeWait">Whether cancellation precedes the first wait registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledObservationPreservesOtherWaiters(bool cancelBeforeWait)
    {
        using Process process = StartInputTarget();
        await VerifyCanceledObservationAsync(process, cancelBeforeWait).ConfigureAwait(false);
    }

    private async Task VerifyCanceledObservationAsync(Process process, bool cancelBeforeWait)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Task canceled = Task.CompletedTask;
        Task retained = Task.CompletedTask;
        try
        {
            if (cancelBeforeWait)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            canceled = DebuggerProcessExit.WaitAsync(process, cancellation.Token);
            retained = DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken);
            if (!cancelBeforeWait)
            {
                Assert.IsFalse(canceled.IsCompleted);
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => canceled.WaitAsync(TestContext.CancellationToken))
                .ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            Assert.IsFalse(process.HasExited);
            Assert.IsFalse(retained.IsCompleted);
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            await retained.ConfigureAwait(false);
            AssertKernelTermination(process);
            Assert.AreEqual(0, process.ExitCode);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await ReleaseTargetAsync(process).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(canceled, retained).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Debug.Assert(canceled.IsCompleted && retained.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Reports association errors instead of treating an unstarted process as already terminated.
    /// </summary>
    [TestMethod]
    public async Task UnstartedProcessDoesNotReportCompletion()
    {
        using var process = new Process();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken)).ConfigureAwait(false);
    }

    private static Process StartInputTarget()
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--wait-for-standard-input");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The owned wait target did not start.");
    }

    private static void AssertKernelTermination(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = new WindowsProcessExitWaitHandle(process.SafeHandle);
            Assert.IsTrue(handle.WaitOne(TimeSpan.Zero), "Process completion must include the Windows kernel signal.");
        }
        Assert.IsTrue(process.HasExited);
    }

    private static async Task ReleaseTargetAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
