namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one managed thread and source-level stepping operation.
/// </summary>
/// <param name="ThreadId">The session-local managed thread identifier.</param>
/// <param name="Kind">The source-level stepping operation.</param>
/// <param name="TargetId">The optional generation-bound Step Into call target.</param>
public sealed record DebugStepRequest(
    int ThreadId,
    DebugStepKind Kind,
    int? TargetId = null);
