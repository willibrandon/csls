namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a managed thread and runtime-approved instruction-pointer destination.
/// </summary>
/// <param name="ThreadId">The managed thread whose active frame will move.</param>
/// <param name="TargetId">The generation-bound goto target identifier.</param>
public sealed record DebugGotoRequest(int ThreadId, int TargetId);
