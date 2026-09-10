namespace Csls.Mcp.Worker;

/// <summary>
/// Carries the ordered debugger sessions owned by one MCP connection.
/// </summary>
/// <param name="Sessions">The current connection-owned sessions and their stop generations.</param>
internal sealed record McpDebugSessionsResult(IReadOnlyList<McpDebugSessionInfo> Sessions);
