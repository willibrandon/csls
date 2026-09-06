namespace Csls.Debugger;

/// <summary>
/// Identifies captured frame storage followed by a bounded chain of array element selections.
/// </summary>
/// <param name="ThreadId">The captured operating-system thread.</param>
/// <param name="StackPointer">The physical frame's stack pointer.</param>
/// <param name="MethodToken">The captured method definition.</param>
/// <param name="Arguments">Whether the root belongs to the arguments scope.</param>
/// <param name="Slot">The root's physical value slot.</param>
/// <param name="ParentId">The containing array handle, or zero for a frame value.</param>
/// <param name="ElementIndex">The flattened element position within the parent.</param>
/// <param name="Depth">The number of array selections from the frame.</param>
internal sealed record CorDebugDumpValuePath(uint ThreadId, ulong StackPointer, uint MethodToken,
    bool Arguments, int Slot, int ParentId = 0, int ElementIndex = 0, int Depth = 0);
