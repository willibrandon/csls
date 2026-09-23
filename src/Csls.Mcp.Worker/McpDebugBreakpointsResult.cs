using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries every authoritative breakpoint for one explicit debugger session.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="State">The debugger lifecycle state observed with the snapshot.</param>
/// <param name="StopGeneration">The latest stop generation, or zero before the first stop.</param>
/// <param name="SourceBreakpoints">Source breakpoint binding states ordered by identifier.</param>
/// <param name="FunctionBreakpoints">Function breakpoint binding states ordered by identifier.</param>
/// <param name="InstructionBreakpoints">Instruction breakpoint binding states ordered by identifier.</param>
/// <param name="ExceptionBreakpoints">Normalized managed-exception policies in evaluation order.</param>
internal sealed record McpDebugBreakpointsResult(
    string DebugSession,
    string State,
    long StopGeneration,
    IReadOnlyList<DebugSourceBreakpointInfo> SourceBreakpoints,
    IReadOnlyList<DebugFunctionBreakpointInfo> FunctionBreakpoints,
    IReadOnlyList<DebugInstructionBreakpointInfo> InstructionBreakpoints,
    IReadOnlyList<McpDebugExceptionBreakpoint> ExceptionBreakpoints) : IMcpDebugSessionResult;
