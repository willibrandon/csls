namespace Csls.Protocol;

/// <summary>
/// Contains hover information and the optional source range it describes.
/// </summary>
public sealed record Hover
{
    /// <summary>
    /// Gets the hover content.
    /// </summary>
    public required MarkupContent Contents { get; init; }

    /// <summary>
    /// Gets the optional source range described by the hover.
    /// </summary>
    public Range? Range { get; init; }
}
