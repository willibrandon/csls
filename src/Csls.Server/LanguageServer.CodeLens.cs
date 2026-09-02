namespace Csls.Server;

public sealed partial class LanguageServer
{
    private const int CodeLensRefreshDelayMilliseconds = 200;
    private readonly Lock _codeLensRefreshGate = new();
    private readonly CancellationTokenSource _codeLensRefreshSource = new();
    private readonly Dictionary<long, Task> _codeLensRefreshTasks = [];
    private long _codeLensRefreshOrdinal;

    private void QueueCodeLensRefresh()
    {
        lock (_codeLensRefreshGate)
        {
            if (!_supportsCodeLensRefresh || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            long ordinal = Interlocked.Increment(ref _codeLensRefreshOrdinal);
            _codeLensRefreshTasks.Add(
                ordinal,
                RefreshCodeLensesAfterDelayAsync(
                    ordinal,
                    _codeLensRefreshSource.Token));
        }
    }

    private async Task RefreshCodeLensesAfterDelayAsync(
        long ordinal,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CodeLensRefreshDelayMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            if (ordinal != Interlocked.Read(ref _codeLensRefreshOrdinal))
            {
                return;
            }

            await _client.RefreshCodeLensesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        catch (InvalidOperationException exception)
        {
            LanguageServerLogger.LogCodeLensRefreshFailure(_logger, exception);
        }
        finally
        {
            lock (_codeLensRefreshGate)
            {
                _codeLensRefreshTasks.Remove(ordinal);
            }
        }
    }

    private async Task StopCodeLensRefreshAsync()
    {
        _ = Interlocked.Increment(ref _codeLensRefreshOrdinal);
        await _codeLensRefreshSource.CancelAsync().ConfigureAwait(false);
        Task[] tasks;
        lock (_codeLensRefreshGate)
        {
            tasks = [.. _codeLensRefreshTasks.Values];
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _codeLensRefreshSource.Dispose();
    }
}
