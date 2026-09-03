namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP managed-exception breakpoint request.
/// </summary>
/// <param name="BreakMode">The stage: thrown, userUnhandled, or unhandled.</param>
/// <param name="ExceptionTypeNames">Exact or base type names; empty selects every type.</param>
internal sealed record McpDebugExceptionBreakpoint(
    string BreakMode,
    IReadOnlyList<string> ExceptionTypeNames);
