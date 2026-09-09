using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Reports bounded resource samples throughout a test-owned native dump capture.
/// </summary>
internal static class DebuggerCaptureResourceObservation
{
    /// <summary>
    /// Samples the collector and its newly created output file until process exit or capture cancellation.
    /// </summary>
    /// <param name="process">The caller-owned collector whose lifetime encloses observation.</param>
    /// <param name="path">The new dump path, with its temporary writer path sampled before publication.</param>
    /// <param name="report">Receives resource measurements for the exact collector process.</param>
    /// <param name="cancellationToken">Cancels sampling before process ownership is released.</param>
    /// <returns>The bounded observation lifetime.</returns>
    internal static async Task ObserveAsync(Process process, string path, Action<string> report,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task exit = DebuggerProcessExit.WaitAsync(process, lifetime.Token);
        Task<bool>? tick = null;
        try
        {
            for (int sample = 0; sample < 64; sample++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.HasExited)
                {
                    break;
                }
                double cpu;
                long resident;
                try
                {
                    cpu = process.TotalProcessorTime.TotalMilliseconds;
                    resident = process.WorkingSet64;
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Resource information can disappear between the exit check and the native process query.
                    break;
                }
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    file = new FileInfo(path + ".tmp");
                }
                long bytes = file.Exists ? file.Length : 0;
                report(FormattableString.Invariant(
                    $"Collector sample {process.Id}: elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms, cpu={cpu:F1} ms, resident={resident} bytes, output={bytes} bytes."));
                tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(exit, tick).ConfigureAwait(false) == exit)
                {
                    await exit.ConfigureAwait(false);
                    break;
                }
                if (!await tick.ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            Task pending = tick is null ? exit : Task.WhenAll(exit, tick);
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
