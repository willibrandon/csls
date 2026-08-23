namespace Csls.Protocol;

/// <summary>
/// Requests completion candidates at one UTF-16 document position.
/// </summary>
public sealed record CompletionParams
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
    /// Gets the optional completion trigger context.
    /// </summary>
    public CompletionContext? Context { get; init; }
}
