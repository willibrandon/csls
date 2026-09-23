using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Identifies resource groups invalidated for one explicit debugger session.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="Kind">The invalidated resource groups.</param>
internal sealed record McpDebuggerResourceChange(
    string DebugSession,
    DebuggerResourceChangeKind Kind);
