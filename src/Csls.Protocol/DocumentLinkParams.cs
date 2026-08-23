namespace Csls.Protocol;

/// <summary>
/// Identifies the document whose navigable resource links are requested.
/// </summary>
public sealed record DocumentLinkParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
