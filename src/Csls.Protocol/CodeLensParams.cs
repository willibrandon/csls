namespace Csls.Protocol;

/// <summary>
/// Identifies the source document requested for code-lens discovery.
/// </summary>
public sealed record CodeLensParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
