using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP-facing target-output segment.
/// </summary>
/// <param name="Sequence">The monotonically increasing session-local sequence.</param>
/// <param name="Category">The target stream name.</param>
/// <param name="Output">The retained output text.</param>
/// <param name="Truncated">Whether text preceding this segment was omitted.</param>
internal sealed record McpDebugOutputEntry(
    long Sequence,
    string Category,
    string Output,
    bool Truncated)
{
    /// <summary>
    /// Projects a private debugger output entry into the MCP contract.
    /// </summary>
    internal static McpDebugOutputEntry Create(DebugOutputEntry entry) => new(
        entry.Sequence,
        entry.Category switch
        {
            DebugOutputCategory.StandardOutput => "standardOutput",
            DebugOutputCategory.StandardError => "standardError",
            _ => throw new InvalidDataException(
                $"Unknown debugger output category {entry.Category}.")
        },
        entry.Output,
        entry.Truncated);
}
