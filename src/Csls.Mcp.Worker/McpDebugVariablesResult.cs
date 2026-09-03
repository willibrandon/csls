using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one current-generation managed variable page.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the values.</param>
/// <param name="Variables">The requested ordered variable page.</param>
internal sealed record McpDebugVariablesResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugVariableInfo> Variables);
