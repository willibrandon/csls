namespace Csls.Protocol;

/// <summary>
/// Selects the symbol for a text document moniker request.
/// </summary>
public sealed record MonikerParams
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
