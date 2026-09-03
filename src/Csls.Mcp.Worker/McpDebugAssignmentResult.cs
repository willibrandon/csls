using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one updated managed value at an unchanged stopped generation.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stopped generation that owns the result.</param>
/// <param name="Variable">The updated immediate variable.</param>
internal sealed record McpDebugAssignmentResult(
    string DebugSession,
    long StopGeneration,
    DebugVariableInfo Variable) : IMcpDebugSessionResult;
