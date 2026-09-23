namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes every authoritative breakpoint configured in one debugger session.
/// </summary>
/// <param name="SourceBreakpoints">Source breakpoint binding states ordered by identifier.</param>
/// <param name="FunctionBreakpoints">Function breakpoint binding states ordered by identifier.</param>
/// <param name="InstructionBreakpoints">Instruction breakpoint binding states ordered by identifier.</param>
/// <param name="ExceptionBreakpoints">Normalized managed-exception policies in evaluation order.</param>
public sealed record DebugBreakpointSnapshot(
    IReadOnlyList<DebugSourceBreakpointInfo> SourceBreakpoints,
    IReadOnlyList<DebugFunctionBreakpointInfo> FunctionBreakpoints,
    IReadOnlyList<DebugInstructionBreakpointInfo> InstructionBreakpoints,
    IReadOnlyList<DebugExceptionBreakpointRequest> ExceptionBreakpoints);
