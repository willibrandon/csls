using Microsoft.Diagnostics.NETCore.Client;

namespace Csls.Debugger.Tests;

/// <summary>
/// Coordinates dump writers that share native snapshot state, storage, and memory bandwidth in one test runner.
/// </summary>
internal static class DebuggerDumpCaptureGate
{
    private static readonly SemaphoreSlim s_memoryCapture = new(1, 1);

    /// <summary>
    /// Runs one heap or full-memory capture at a time while allowing smaller dump policies to proceed independently.
    /// </summary>
    /// <param name="captureType">The dump writer policy selected by the test.</param>
    /// <param name="operation">The real dump capture operation.</param>
    /// <param name="cancellationToken">Cancels acquisition without retaining the shared capture slot.</param>
    /// <returns>Completion after the dump writer releases the shared capture slot.</returns>
    internal static async Task RunMemoryCaptureAsync(
        DumpType captureType,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (captureType is not (DumpType.WithHeap or DumpType.Full))
        {
            await operation().ConfigureAwait(false);
            return;
        }

        await s_memoryCapture.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            s_memoryCapture.Release();
        }
    }

}
