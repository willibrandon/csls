using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries current-generation scopes for one managed stack frame.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the scopes.</param>
/// <param name="Scopes">The ordered frame scopes.</param>
internal sealed record McpDebugScopesResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugScopeInfo> Scopes);
