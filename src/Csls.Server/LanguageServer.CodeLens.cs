namespace Csls.Server;

public sealed partial class LanguageServer
{
    private const int CodeLensRefreshDelayMilliseconds = 200;
    private readonly Lock _codeLensRefreshGate = new();
    private readonly CancellationTokenSource _codeLensRefreshSource = new();
    private readonly Dictionary<long, Task> _codeLensRefreshTasks = [];
    private long _codeLensRefreshOrdinal;
    private int _codeLensRefreshStopState;

    private void QueueCodeLensRefresh()
    {
        lock (_codeLensRefreshGate)
        {
            if (!_supportsCodeLensRefresh ||
                Volatile.Read(ref _codeLensRefreshStopState) != 0 ||
                LifecycleState is not ServerLifecycleState.Running)
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

            cancellationToken.ThrowIfCancellationRequested();
            if (LifecycleState is not ServerLifecycleState.Running)
            {
                return;
            }

            await _client.RefreshCodeLensesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsCodeLensRefreshStopping(cancellationToken))
        {
            return;
        }
        catch (ObjectDisposedException) when (IsCodeLensRefreshStopping(cancellationToken))
        {
            return;
        }
        catch (InvalidOperationException) when (IsCodeLensRefreshStopping(cancellationToken))
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
        BeginCodeLensRefreshStop();
        Task[] tasks;
        lock (_codeLensRefreshGate)
        {
            tasks = [.. _codeLensRefreshTasks.Values];
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _codeLensRefreshSource.Dispose();
    }

    private void BeginCodeLensRefreshStop()
    {
        if (Interlocked.Exchange(ref _codeLensRefreshStopState, 1) != 0)
        {
            return;
        }

        _ = Interlocked.Increment(ref _codeLensRefreshOrdinal);
        _codeLensRefreshSource.Cancel();
    }

    private bool IsCodeLensRefreshStopping(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ||
        Volatile.Read(ref _codeLensRefreshStopState) != 0 ||
        LifecycleState is not ServerLifecycleState.Running ||
        Volatile.Read(ref _disposeState) != 0;
}
