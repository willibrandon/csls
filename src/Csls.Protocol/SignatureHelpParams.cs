namespace Csls.Protocol;

/// <summary>
/// Identifies a document position and client context for signature help.
/// </summary>
public sealed record SignatureHelpParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 document position.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the optional signature help trigger context.
    /// </summary>
    public SignatureHelpContext? Context { get; init; }
}
