namespace Csls.Protocol;

/// <summary>
/// Identifies the document position for a call-hierarchy prepare request.
/// </summary>
public sealed record CallHierarchyPrepareParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 position.
    /// </summary>
    public required Position Position { get; init; }
}
