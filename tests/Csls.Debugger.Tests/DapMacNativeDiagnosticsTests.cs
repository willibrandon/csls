using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies concurrent macOS diagnostics retain native stacks and preserve owned process streams.
/// </summary>
[TestClass]
public sealed class DapMacNativeDiagnosticsTests : DapTestContext
{
    /// <summary>
    /// Captures independent native processes concurrently and verifies their sampled identities and subsequent input.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.OSX)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ConcurrentNativeCapturesPreserveOwnedProcesses()
    {
        await Task.WhenAll(CaptureOwnedProcessAsync(), CaptureOwnedProcessAsync()).ConfigureAwait(false);
    }

    private async Task CaptureOwnedProcessAsync()
    {
        var start = new ProcessStartInfo("/bin/cat")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The native diagnostic target did not start.");
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await AssertEchoAsync(process, "before-capture").ConfigureAwait(false);
            await DebuggerProcessDiagnostics.CaptureAsync(process.Id, TestContext).ConfigureAwait(false);
            string directory = Assert.ContainsSingle(Directory.EnumerateDirectories(
                Path.Join(FindRepositoryRoot(), "artifacts", "test-results"),
                $"native-stacks-{process.Id.ToString(CultureInfo.InvariantCulture)}-*"));
            string report = await File.ReadAllTextAsync(Path.Join(directory, $"process-{process.Id}.sample.txt"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains($"[{process.Id.ToString(CultureInfo.InvariantCulture)}]", report);
            Assert.Contains("Call graph:", report);
            Assert.Contains("read", report);
            Assert.IsFalse(process.HasExited);
            await AssertEchoAsync(process, "after-capture").ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode);
            Assert.IsEmpty(await error.ConfigureAwait(false));
            Assert.IsEmpty(await process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken)
                .ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
        }
    }

    private async Task AssertEchoAsync(Process process, string value)
    {
        await process.StandardInput.WriteLineAsync(value.AsMemory(), TestContext.CancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(value, await process.StandardOutput.ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }
}
