using Csls.Debugger.Interop;

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
    /// Waits until CoreCLR reports and resumes the initial create-process stop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the initial managed callback.</param>
    /// <returns>A task that completes after the runtime accepts Continue.</returns>
    internal async Task WaitForCreateProcessAsync(CancellationToken cancellationToken)
    {
        int result = await _createProcessCompletion.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Continue");
    }

    /// <summary>
    /// Waits until CoreCLR delivers the terminal process callback on the engine actor.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for callback delivery.</param>
    /// <returns>A task that completes after the callback relinquishes its process pointer.</returns>
    internal Task WaitForExitProcessAsync(CancellationToken cancellationToken) =>
        _exitProcessCompletion.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Prevents queued runtime callbacks from resuming a target that is being detached.
    /// </summary>
    internal void BeginDetach() => Volatile.Write(ref _detaching, 1);

    /// <summary>
    /// Allows managed callbacks to resume the target after a failed detachment attempt.
    /// </summary>
    internal void CancelDetach() => Volatile.Write(ref _detaching, 0);

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
