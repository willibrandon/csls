namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP managed-function breakpoint request.
/// </summary>
/// <param name="Name">The method or fully qualified type-and-method name.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
internal sealed record McpDebugFunctionBreakpoint(
    string Name,
    string? HitCondition = null);
