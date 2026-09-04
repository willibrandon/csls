namespace Csls.Debugger;

/// <summary>
/// Publishes unrecoverable runtime failures without calling disabled debugging services.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private readonly TaskCompletionSource<CorDebugRuntimeException> _runtimeFailureCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CorDebugRuntimeException? _runtimeFailure;

    /// <summary>
    /// Gets the first error that disabled runtime debugging services, when one was reported.
    /// </summary>
    internal CorDebugRuntimeException? RuntimeFailure => Volatile.Read(ref _runtimeFailure);

    /// <summary>
    /// Waits for the runtime to report an unrecoverable debugging-services failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels observation without modifying runtime state.</param>
    /// <returns>The original runtime error.</returns>
    internal Task<CorDebugRuntimeException> WaitForRuntimeFailureAsync(
        CancellationToken cancellationToken) =>
        _runtimeFailureCompletion.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Rejects further runtime operations after debugging services have been disabled.
    /// </summary>
    internal void ThrowIfRuntimeFailed()
    {
        if (RuntimeFailure is CorDebugRuntimeException failure)
        {
            throw failure;
        }
    }

    private void RecordRuntimeFailure(int errorHResult, uint errorCode)
    {
        var failure = new CorDebugRuntimeException(errorHResult, errorCode);
        if (Interlocked.CompareExchange(ref _runtimeFailure, failure, null) is not null)
        {
            return;
        }

        _ = _runtimeFailureCompletion.TrySetResult(failure);
        _ = _createProcessCompletion.TrySetException(failure);
        // Disabled services cannot deliver the normal final managed callback.
        _ = _exitProcessCompletion.TrySetResult();
    }
}
