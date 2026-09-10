using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;

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
    /// Starts bounded reader sampling at collector exit while its caller continues draining output.
    /// </summary>
    /// <param name="collector">The caller-owned collector whose kernel exit triggers sampling.</param>
    /// <param name="cancellationToken">Bounds exit observation, sampling, and symbol rundown.</param>
    /// <returns>Completion after collector exit starts an independently owned sampling operation.</returns>
    internal async Task ObserveExitAsync(Process collector, CancellationToken cancellationToken)
    {
        await DebuggerProcessExit.WaitAsync(collector, cancellationToken).ConfigureAwait(false);
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
}
