namespace Csls.Protocol;

/// <summary>
/// Selects one document position for a linked editing range request.
/// </summary>
public sealed record LinkedEditingRangeParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 document position.
    /// </summary>
    public required Position Position { get; init; }
}
