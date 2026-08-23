namespace Csls.Protocol;

/// <summary>
/// Identifies the visible document range for an inlay-hint request.
/// </summary>
public sealed record InlayHintParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the visible UTF-16 range.
    /// </summary>
    public required Range Range { get; init; }
}
