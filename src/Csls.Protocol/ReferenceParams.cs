namespace Csls.Protocol;

/// <summary>
/// Requests symbol references at one UTF-16 document position.
/// </summary>
public sealed record ReferenceParams
{
    /// <summary>
    /// Gets the target document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 position.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets declaration inclusion behavior.
    /// </summary>
    public required ReferenceContext Context { get; init; }
}
