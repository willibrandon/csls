namespace Csls.Debugger;

/// <summary>
/// Exposes and releases the native managed-callback lifetime.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    /// <summary>
    /// Marks the callback pipeline as executing one debugger-owned function evaluation.
    /// </summary>
    internal void BeginFunctionEvaluation()
    {
        if (Interlocked.CompareExchange(ref _functionEvaluationActive, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one managed function evaluation may run at a time.");
        }
    }

    /// <summary>
    /// Restores ordinary callback handling after debugger-owned function evaluation.
    /// </summary>
    internal void EndFunctionEvaluation() =>
        Volatile.Write(ref _functionEvaluationActive, 0);

    private bool IsFunctionEvaluationActive =>
        Volatile.Read(ref _functionEvaluationActive) != 0;

    /// <summary>
    /// Gets the COM interface pointer accepted by ICorDebug.SetManagedHandler.
    /// </summary>
    internal nint Pointer => Volatile.Read(ref _instance);

    /// <summary>
    /// Waits until CoreCLR's initial process, module, and thread callbacks have drained.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for runtime initialization.</param>
    /// <returns>A task that completes after the last startup continuation succeeds.</returns>
    internal Task WaitForInitializationAsync(CancellationToken cancellationToken) =>
        _initialization.WaitAsync(cancellationToken);

    /// <summary>
    /// Waits until CoreCLR delivers its terminal callback or disables debugging services.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for callback delivery.</param>
    /// <returns>A task that completes after the callback relinquishes its process pointer.</returns>
    internal Task WaitForExitProcessAsync(CancellationToken cancellationToken) =>
        _exitProcessCompletion.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Gets whether terminal callback handling has released the runtime process pointer.
    /// </summary>
    internal bool HasCompletedExit => _exitProcessCompletion.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Prevents queued runtime callbacks from resuming a target that is being detached.
    /// </summary>
    internal void BeginDetach() => Volatile.Write(ref _detaching, 1);

    /// <summary>
    /// Allows managed callbacks to resume the target after a failed detachment attempt.
    /// </summary>
    internal void CancelDetach() => Volatile.Write(ref _detaching, 0);

    /// <summary>
    /// Retires runtime work while retaining terminal callback delivery and native reference release.
    /// </summary>
    internal void RetireProcess() => Volatile.Write(ref _processRetired, 1);

    private bool CanDispatchCallbacks =>
        Volatile.Read(ref _detaching) == 0 &&
        Volatile.Read(ref _processRetired) == 0 &&
        RuntimeFailure is null;

    /// <inheritdoc />
    public void Dispose()
    {
        nint instance = Interlocked.Exchange(ref _instance, 0);
        if (instance != 0)
        {
            _ = ReleaseCore(instance);
        }
    }
}
