namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP managed-IL instruction breakpoint request.
/// </summary>
/// <param name="InstructionReference">The opaque generation-bound IL reference.</param>
/// <param name="Offset">The signed byte offset from the reference.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
internal sealed record McpDebugInstructionBreakpoint(
    string InstructionReference,
    long Offset = 0,
    string? HitCondition = null);
