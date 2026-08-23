namespace Csls.Protocol;

/// <summary>
/// Identifies a document and the editor preferences used to format it.
/// </summary>
public sealed record DocumentFormattingParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the editor formatting preferences.
    /// </summary>
    public required FormattingOptions Options { get; init; }
}
