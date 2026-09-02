namespace Csls.Debugger;

/// <summary>
/// Represents one cancelable operation serialized by the debugger actor.
/// </summary>
internal sealed class DebuggerSessionWorkItem
{
    private readonly Func<CancellationToken, ValueTask> _operation;
    private readonly CancellationToken _callerCancellationToken;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Creates one debugger actor operation.
    /// </summary>
    /// <param name="operation">The operation to execute on the actor.</param>
    /// <param name="callerCancellationToken">Cancels the queued or running operation.</param>
    internal DebuggerSessionWorkItem(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken callerCancellationToken)
    {
        _operation = operation;
        _callerCancellationToken = callerCancellationToken;
    }

    /// <summary>
    /// Gets the completion observed by the operation caller.
    /// </summary>
    internal Task Completion => _completion.Task;

    /// <summary>
    /// Executes the operation with caller and actor cancellation linked.
    /// </summary>
    /// <param name="actorCancellationToken">Cancels the complete actor.</param>
    /// <returns>A task that completes after caller completion is published.</returns>
    internal async ValueTask ExecuteAsync(CancellationToken actorCancellationToken)
    {
        if (_callerCancellationToken.IsCancellationRequested)
        {
            _ = _completion.TrySetCanceled(_callerCancellationToken);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _callerCancellationToken,
            actorCancellationToken);
        try
        {
            await _operation(linked.Token).ConfigureAwait(false);
            _ = _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_callerCancellationToken.IsCancellationRequested)
        {
            _ = _completion.TrySetCanceled(_callerCancellationToken);
        }
        catch (OperationCanceledException) when (actorCancellationToken.IsCancellationRequested)
        {
            _ = _completion.TrySetCanceled(actorCancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = _completion.TrySetException(exception);
        }
    }
}
