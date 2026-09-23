using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one current-generation managed expression result.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the result.</param>
/// <param name="Evaluation">The formatted expression result.</param>
internal sealed record McpDebugEvaluationResult(
    string DebugSession,
    long StopGeneration,
    DebugEvaluateResult Evaluation) : IMcpDebugSessionResult;
