namespace Csls.Protocol;

/// <summary>
/// Applies one ordered full-text or incremental mutation to an opened document.
/// </summary>
public sealed record TextDocumentContentChangeEvent
{
    /// <summary>
    /// Gets the replaced range, or null when the text replaces the complete document.
    /// </summary>
    public Range? Range { get; init; }

    /// <summary>
    /// Gets the optional replaced UTF-16 code-unit count supplied by legacy clients.
    /// </summary>
    public int? RangeLength { get; init; }

    /// <summary>
    /// Gets the replacement text.
    /// </summary>
    public required string Text { get; init; }
}
