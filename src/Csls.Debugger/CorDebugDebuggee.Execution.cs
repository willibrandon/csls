using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Controls managed execution and source-level stepping.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Stops all managed threads at a runtime-consistent inspection point.
    /// </summary>
    internal void Pause()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Stop(dwTimeoutIgnored: 0),
            "ICorDebugController.Stop");
    }

    /// <summary>
    /// Resumes all managed threads from the current debugger stop.
    /// </summary>
    internal void Continue()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
            "ICorDebugController.Continue");
    }

}
