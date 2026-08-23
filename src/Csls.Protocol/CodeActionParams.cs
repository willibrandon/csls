namespace Csls.Protocol;

/// <summary>
/// Identifies a document range and the editor's requested code-action context.
/// </summary>
public sealed record CodeActionParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 source range.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets diagnostics and requested action categories.
    /// </summary>
    public required CodeActionContext Context { get; init; }
}
