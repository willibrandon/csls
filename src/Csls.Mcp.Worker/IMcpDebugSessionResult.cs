namespace Csls.Mcp.Worker;

/// <summary>
/// Identifies structured MCP results belonging to one explicit debugger session.
/// </summary>
internal interface IMcpDebugSessionResult
{
    /// <summary>
    /// Gets the exact debugger-session identifier.
    /// </summary>
    string DebugSession { get; }
}
