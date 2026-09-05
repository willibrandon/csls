namespace Csls.Debugger;

/// <summary>
/// Releases ownership after CoreCLR disables its debugging services.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private int _runtimeAbandoned;

    /// <summary>
    /// Gets the unrecoverable error reported by the native runtime, when present.
    /// </summary>
    internal CorDebugRuntimeException? RuntimeFailure => _managedCallback.RuntimeFailure;

    /// <summary>
    /// Releases retained interfaces without invoking unavailable runtime operations.
    /// </summary>
    /// <param name="failure">The original error propagated to any pending evaluation.</param>
    internal void AbandonFailedRuntime(CorDebugRuntimeException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (Interlocked.Exchange(ref _runtimeAbandoned, 1) != 0)
        {
            return;
        }

        FailFunctionEvaluation(failure, runtimeAvailable: false);
        ClearFrameHandles();
        CancelStep(runtimeAvailable: false);
        _sourceBreakpoints.ResetRuntimeBindings(runtimeAvailable: false);
        _functionBreakpoints.ResetRuntimeBindings(runtimeAvailable: false);
        _instructionBreakpoints.ResetRuntimeBindings(runtimeAvailable: false);
        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        ReleaseRuntimeReferences(corDebug, debugProcess, runtimeAvailable: false);
        Volatile.Write(ref _detached, 1);
    }
}
