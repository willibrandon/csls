using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Controls managed execution and source-level stepping.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Synchronizes the runtime and releases transient execution handles before detachment.
    /// </summary>
    /// <returns>True when this operation stopped a previously running target.</returns>
    internal unsafe bool PrepareForDetach()
    {
        _managedCallback.ThrowIfRuntimeFailed();
        _managedCallback.BeginDetach();
        var controller = new ICorDebugControllerAbi(_debugProcess);
        int isRunning = 0;
        int* isRunningAddress = &isRunning;
        CorDebugHResult.ThrowIfFailed(
            controller.IsRunning((nint)isRunningAddress),
            "ICorDebugController.IsRunning");
        isRunning = Volatile.Read(ref *isRunningAddress);
        if (isRunning != 0)
        {
            CorDebugHResult.ThrowIfFailed(
                controller.Stop(dwTimeoutIgnored: 0),
                "ICorDebugController.Stop");
        }

        CancelStep();
        ClearFrameHandles();
        return isRunning != 0;
    }

    /// <summary>
    /// Restores managed callback continuation after a failed detachment attempt.
    /// </summary>
    /// <param name="resume">Whether preparation stopped a previously running target.</param>
    internal void CancelDetach(bool resume)
    {
        _managedCallback.ThrowIfRuntimeFailed();
        _managedCallback.CancelDetach();
        if (resume && Volatile.Read(ref _debugProcess) != 0)
        {
            Continue();
        }
    }

    /// <summary>
    /// Stops all managed threads at a runtime-consistent inspection point.
    /// </summary>
    internal void Pause()
    {
        _managedCallback.ThrowIfRuntimeFailed();
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Stop(dwTimeoutIgnored: 0),
            "ICorDebugController.Stop");
    }

    /// <summary>
    /// Resumes all managed threads from the current debugger stop.
    /// </summary>
    internal void Continue() => ContinueCore(preserveFrameIdentity: false);

    private void ContinueForFunctionEvaluation() => ContinueCore(preserveFrameIdentity: true);

    private void ContinueCore(bool preserveFrameIdentity)
    {
        _managedCallback.ThrowIfRuntimeFailed();
        ClearFrameHandles(preserveFrameIdentity);
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
            "ICorDebugController.Continue");
    }

}
