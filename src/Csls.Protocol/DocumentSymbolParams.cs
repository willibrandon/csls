namespace Csls.Protocol;

/// <summary>
/// Identifies the document whose declaration hierarchy is requested.
/// </summary>
public sealed record DocumentSymbolParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
