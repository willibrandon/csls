using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies live diagnostic visibility, process cleanup, and bounded files through real capture streams.
/// </summary>
[TestClass]
public sealed class DebuggerCaptureTraceTests : DapTestContext
{
    /// <summary>
    /// Reads a captured child announcement before exit and retains it when observation is canceled or the child exits.
    /// </summary>
    /// <param name="cancel">Whether cancellation ends the capture instead of independent process termination.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecordsRemainReadableDuringCaptureAndAfterCancellation(bool cancel)
    {
        string directory = Directory.CreateTempSubdirectory("csls-live-capture-log-").FullName;
        try
        {
            using var trace = new DebuggerCaptureTrace(Path.Join(directory, "capture.log"));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            var announced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            start.ArgumentList.Add(ResolveTestProcessHost());
            start.ArgumentList.Add("--debugger-process-tree-child");
            start.ArgumentList.Add("leaf");
            Task running = DebuggerTestProcess.RunAsync(start, cancellation.Token, record =>
            {
                trace.WriteLine(record);
                announced.TrySetResult(int.Parse(record, CultureInfo.InvariantCulture));
            });
            try
            {
                int id = await announced.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                using var process = Process.GetProcessById(id);
                _ = process.SafeHandle;
                Assert.IsFalse(process.HasExited);
                Assert.IsFalse(running.IsCompleted);
                string expected = id.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
                Assert.AreEqual(expected, await ReadTraceAsync(trace.FilePath, TestContext.CancellationToken)
                    .ConfigureAwait(false));
                if (cancel)
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                    OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await running.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.AreEqual(cancellation.Token, exception.CancellationToken);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                    await running.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                }
                Assert.IsTrue(process.HasExited);
                Assert.AreEqual(expected, await ReadTraceAsync(trace.FilePath, TestContext.CancellationToken)
                    .ConfigureAwait(false));
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserves complete records at the budget boundary and writes exactly one truncation marker for excessive output.
    /// </summary>
    /// <param name="delta">The hostile record's distance from the exact character budget.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    public async Task ExcessiveRecordsProduceOneBoundedMarker(int delta)
    {
        string directory = Directory.CreateTempSubdirectory("csls-bounded-capture-log-").FullName;
        try
        {
            using var trace = new DebuggerCaptureTrace(Path.Join(directory, "capture.log"));
            string record = new('\u03bb', 65536 - Environment.NewLine.Length + delta);
            trace.WriteLine(record);
            string marker = "Capture log truncated." + Environment.NewLine;
            string prefix = delta > 0 ? string.Empty : record + Environment.NewLine;
            Assert.AreEqual(delta > 0 ? marker : prefix,
                await ReadTraceAsync(trace.FilePath, TestContext.CancellationToken).ConfigureAwait(false));
            trace.WriteLine(record);
            trace.WriteLine(record);
            Assert.AreEqual(prefix + marker,
                await ReadTraceAsync(trace.FilePath, TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadTraceAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
