namespace Csls.Protocol;

/// <summary>
/// Identifies whether completion insertion text is plain text or an LSP snippet.
/// </summary>
public enum InsertTextFormat
{
    /// <summary>
    /// Leaves completion text interpretation unspecified on the wire.
    /// </summary>
    None = 0,

    /// <summary>
    /// Inserts the completion text without snippet interpretation.
    /// </summary>
    PlainText = 1,

    /// <summary>
    /// Interprets the completion text using LSP snippet syntax.
    /// </summary>
    Snippet = 2
}
