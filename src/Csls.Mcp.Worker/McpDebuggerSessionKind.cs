namespace Csls.Mcp.Worker;

/// <summary>
/// Identifies how an MCP debugger session acquired its target.
/// </summary>
internal enum McpDebuggerSessionKind
{
    /// <summary>
    /// The debugger launched and owns the target process.
    /// </summary>
    Launch,

    /// <summary>
    /// The debugger attached to an independently owned target process.
    /// </summary>
    Attach
}
