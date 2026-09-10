using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Samples the test host's managed readers after an independently observed collector exits.
/// </summary>
internal sealed class DebuggerCollectorStackCapture : IAsyncDisposable
{
    private static readonly SemaphoreSlim s_samplingGate = new(1, 1);
    private readonly TestContext _testContext;
    private readonly List<Task> _captures = [];

    /// <summary>
    /// Owns background reader sampling until the test has released its collectors.
    /// </summary>
    /// <param name="testContext">The test context retaining trace artifacts and sampling diagnostics.</param>
    internal DebuggerCollectorStackCapture(TestContext testContext) => _testContext = testContext;

    /// <summary>
    /// Starts bounded reader sampling after collector exit while its caller continues draining output.
    /// </summary>
    /// <param name="collector">The caller-owned collector whose kernel exit has already been observed.</param>
    /// <param name="cancellationToken">Bounds sampling and symbol rundown.</param>
    internal void ObserveOutputDrain(Process collector, CancellationToken cancellationToken)
    {
        _captures.Add(CaptureAsync(collector.Id, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await Task.WhenAll(_captures).ConfigureAwait(false);

    private async Task CaptureAsync(int collectorId, CancellationToken cancellationToken)
    {
        string path = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results",
            $"collector-readers-{Environment.ProcessId}-{collectorId}-{Guid.NewGuid():N}.nettrace");
        bool sampling = false;
        try
        {
            // Concurrent collectors share the same host; one sample captures all of its managed readers.
            sampling = await s_samplingGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!sampling)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("The reader trace has no artifact directory."));
            _testContext.WriteLine($"Sampling test host {Environment.ProcessId} after collector {collectorId} exited: {path}.");
            if (OperatingSystem.IsWindows())
            {
                // Native snapshot collection does not wait for the host's managed diagnostic server.
                await CaptureNativeReadersAsync(Path.ChangeExtension(path, ".dmp"), cancellationToken).ConfigureAwait(false);
            }
            await DebuggerManagedStackCapture.CaptureAsync(Environment.ProcessId, path, cancellationToken)
                .ConfigureAwait(false);
            _testContext.WriteLine($"Completed test-host reader trace: {path}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or DiagnosticsClientException or InvalidOperationException or Win32Exception)
        {
            _testContext.WriteLine($"Test-host reader capture: {exception.Message}");
        }
        finally
        {
            if (sampling)
            {
                s_samplingGate.Release();
            }
            if (File.Exists(path))
            {
                _testContext.AddResultFile(path);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task CaptureNativeReadersAsync(string path, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            using var host = Process.GetCurrentProcess();
            (int exitCode, string output, string error) = await WindowsDebuggerProcessCapture.CaptureAsync(
                host, path, cancellationToken).ConfigureAwait(false);
            _testContext.WriteLine($"Native test-host reader capture exited with {exitCode} after " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms: {output}{error}");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _testContext.WriteLine($"Native test-host reader capture: {exception.Message}");
        }
        finally
        {
            if (File.Exists(path))
            {
                _testContext.AddResultFile(path);
            }
        }
    }
}
