namespace Csls.Debugger.Contracts;

/// <summary>
/// Reports bounded traversal progress and debugger-owned native references for one stack inspection.
/// </summary>
/// <param name="ThreadId">The managed thread being inspected.</param>
/// <param name="InspectedFrames">The number of managed frames actually visited, including skipped frames.</param>
/// <param name="CapturedFrames">The number of frames selected for this response.</param>
/// <param name="RetainedFrameBindings">The native frame bindings currently owned by the stopped-session registry.</param>
/// <param name="OwnedWalkInterfaces">The native interface references currently owned by this stack walker.</param>
/// <param name="State">Whether traversal is active or has completed, canceled, or failed.</param>
public sealed record DebugStackWalkProgress(
    int ThreadId,
    int InspectedFrames,
    int CapturedFrames,
    int RetainedFrameBindings,
    int OwnedWalkInterfaces,
    DebugStackWalkState State);
