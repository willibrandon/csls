namespace Csls.Protocol;

/// <summary>
/// Identifies a document range and the editor preferences used to format it.
/// </summary>
public sealed record DocumentRangeFormattingParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the document range whose whitespace should be formatted.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the editor formatting preferences.
    /// </summary>
    public required FormattingOptions Options { get; init; }
}
