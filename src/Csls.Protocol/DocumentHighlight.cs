namespace Csls.Protocol;

/// <summary>
/// Describes one semantic occurrence of a symbol in a source document.
/// </summary>
public sealed record DocumentHighlight
{
    /// <summary>
    /// Gets the exact source occurrence range.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets whether the occurrence is textual, read, or written.
    /// </summary>
    public DocumentHighlightKind? Kind { get; init; }
}
