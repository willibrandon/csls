namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one bounded target-output segment retained by debugger control.
/// </summary>
/// <param name="Sequence">The monotonically increasing session-local sequence.</param>
/// <param name="Category">The target stream that produced the segment.</param>
/// <param name="Output">The retained output text.</param>
/// <param name="Truncated">Whether text preceding this segment was omitted.</param>
public sealed record DebugOutputEntry(
    long Sequence,
    DebugOutputCategory Category,
    string Output,
    bool Truncated);
