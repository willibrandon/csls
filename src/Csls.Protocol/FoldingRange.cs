namespace Csls.Protocol;

/// <summary>
/// Describes one foldable source range using zero-based UTF-16 positions.
/// </summary>
public sealed record FoldingRange
{
    /// <summary>
    /// Gets the zero-based line where folding starts.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Gets the optional UTF-16 character where folding starts.
    /// </summary>
    public int? StartCharacter { get; init; }

    /// <summary>
    /// Gets the zero-based line where folding ends.
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// Gets the optional UTF-16 character where folding ends.
    /// </summary>
    public int? EndCharacter { get; init; }

    /// <summary>
    /// Gets the standard semantic category when the client supports it.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Gets the optional text displayed while the range is collapsed.
    /// </summary>
    public string? CollapsedText { get; init; }
}
