using System.Collections.Concurrent;
using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies capture resource samples against real files and independently owned process lifetimes.
/// </summary>
[TestClass]
public sealed class DebuggerCaptureResourceObservationTests : DapTestContext
{
    /// <summary>
    /// Reports the actual collector identity and file extent before completing on process exit or observer cancellation.
    /// </summary>
    /// <param name="storage">Whether the real capture file is absent, temporary, or published.</param>
    /// <param name="cancel">Whether observation ends while the independently owned process remains alive.</param>
    [TestMethod]
    [DataRow("absent", false)]
    [DataRow("temporary", false)]
    [DataRow("published", false)]
    [DataRow("absent", true)]
    [DataRow("temporary", true)]
    [DataRow("published", true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SamplesActualFilesAndHonorsProcessLifetime(string storage, bool cancel)
    {
        string directory = Directory.CreateTempSubdirectory("csls-capture-resources-").FullName;
        try
        {
            string path = Path.Join(directory, "capture.dmp");
            long expectedBytes = 0;
            if (storage != "absent")
            {
                string actualPath = storage == "temporary" ? path + ".tmp" : path;
                File.Copy(ResolveTestProcessHost(), actualPath);
                expectedBytes = new FileInfo(actualPath).Length;
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(ResolveTestProcessHost());
            start.ArgumentList.Add("--debugger-process-tree-child");
            start.ArgumentList.Add("leaf");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The fixture did not start.");
            await ObserveOwnedProcessAsync(process, path, expectedBytes, cancel).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task ObserveOwnedProcessAsync(Process process, string path, long expectedBytes, bool cancel)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var records = new ConcurrentQueue<string>();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observation = DebuggerCaptureResourceObservation.ObserveAsync(process, path, line =>
        {
            records.Enqueue(line);
            sampled.TrySetResult();
        }, cancellation.Token);
        try
        {
            await sampled.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(process.HasExited);
            Assert.IsFalse(observation.IsCompleted);
            string first = records.First();
            Assert.StartsWith($"Collector sample {process.Id}: elapsed=", first);
            Assert.Contains(" ms, cpu=", first);
            Assert.Contains(" ms, resident=", first);
            Assert.EndsWith($"output={expectedBytes} bytes.", first);
            if (cancel)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await observation.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
                Assert.IsFalse(process.HasExited, "Canceling resource observation must preserve the caller-owned target.");
            }
            else
            {
                process.Kill(entireProcessTree: true);
                await observation.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(process.HasExited);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            int finishedRecords = records.Count;
            await cancellation.CancelAsync().ConfigureAwait(false);
            Assert.HasCount(finishedRecords, records);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await DebuggerProcessExit.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
            await observation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Task streams = Task.WhenAll(output, error);
            await streams.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
