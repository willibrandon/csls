using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one updated managed value and the stop generation that owns it.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stopped generation that owns the result.</param>
/// <param name="TargetCodeExecuted">Whether assignment resumed the target to materialize its value.</param>
/// <param name="Variable">The updated immediate variable.</param>
internal sealed record McpDebugAssignmentResult(
    string DebugSession,
    long StopGeneration,
    bool TargetCodeExecuted,
    DebugVariableInfo Variable) : IMcpDebugSessionResult;
