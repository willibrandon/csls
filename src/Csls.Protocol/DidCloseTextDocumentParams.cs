namespace Csls.Protocol;

/// <summary>
/// Reports that a client no longer owns an open text document overlay.
/// </summary>
public sealed record DidCloseTextDocumentParams
{
    /// <summary>
    /// Gets the closed text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
