namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one stable recoverable debugger tool error.
/// </summary>
/// <param name="Code">The stable machine-readable debugger error code.</param>
/// <param name="Message">The bounded human-readable corrective message.</param>
internal sealed record McpDebuggerError(string Code, string Message);
