using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics.Tracing;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures bounded EventPipe stack samples and symbol rundown from a test-owned process.
/// </summary>
internal static class DebuggerManagedStackCapture
{
    private const int MaximumTraceBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Writes a short managed-stack trace while observing the caller's diagnostic deadline.
    /// </summary>
    /// <param name="processId">The previously identified test-owned process.</param>
    /// <param name="path">The new trace artifact path.</param>
    /// <param name="cancellationToken">Bounds diagnostic connection, collection, and rundown.</param>
    /// <returns>A task that completes when the trace has drained and its streams have closed.</returns>
    internal static async Task CaptureAsync(int processId, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = new DiagnosticsClient(processId);
        using EventPipeSession session = await client.StartEventPipeSessionAsync(
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            requestRundown: true, circularBufferMB: 4, token: cancellationToken).ConfigureAwait(false);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 65536, FileOptions.Asynchronous);
        await Task.WhenAll(
            CopyTraceAsync(session.EventStream, output, cancellationToken),
            StopAfterSamplingAsync(session, cancellationToken)).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopAfterSamplingAsync(EventPipeSession session, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static async Task CopyTraceAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65536];
        int total = 0;
        while (await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is int count && count != 0)
        {
            total = checked(total + count);
            if (total > MaximumTraceBytes)
            {
                throw new IOException("Managed stack capture exceeded its sixteen-megabyte artifact limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
