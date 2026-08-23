namespace Csls.Protocol;

/// <summary>
/// Identifies a document and prior semantic-token result to update.
/// </summary>
public sealed record SemanticTokensDeltaParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the opaque identifier of the previously received result.
    /// </summary>
    public required string PreviousResultId { get; init; }
}
