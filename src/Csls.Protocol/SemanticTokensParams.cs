namespace Csls.Protocol;

/// <summary>
/// Identifies a document whose complete semantic tokens are requested.
/// </summary>
public sealed record SemanticTokensParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
