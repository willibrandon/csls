namespace Csls.Protocol;

/// <summary>
/// Describes equal, non-overlapping source ranges that clients edit together.
/// </summary>
public sealed record LinkedEditingRanges
{
    /// <summary>
    /// Gets the equal source ranges that participate in linked editing.
    /// </summary>
    public required IReadOnlyList<Range> Ranges { get; init; }

    /// <summary>
    /// Gets the optional regular expression that validates replacement text.
    /// </summary>
    public string? WordPattern { get; init; }
}
