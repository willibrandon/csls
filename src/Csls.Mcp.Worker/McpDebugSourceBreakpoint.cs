namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP source-breakpoint request.
/// </summary>
/// <param name="Line">The positive one-based source line.</param>
/// <param name="Column">The optional positive one-based source column.</param>
/// <param name="Condition">The optional source-language Boolean condition.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
/// <param name="LogMessage">The optional interpolated message that replaces stopping.</param>
internal sealed record McpDebugSourceBreakpoint(
    int Line,
    int? Column = null,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null);
