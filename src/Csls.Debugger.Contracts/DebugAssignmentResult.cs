namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one completed managed assignment and its resulting stop generation.
/// </summary>
/// <param name="StopGeneration">The stopped generation that owns the updated value.</param>
/// <param name="TargetCodeExecuted">Whether assignment resumed the target to materialize its value.</param>
/// <param name="Variable">The updated immediate variable.</param>
public sealed record DebugAssignmentResult(
    long StopGeneration,
    bool TargetCodeExecuted,
    DebugVariableInfo Variable);
