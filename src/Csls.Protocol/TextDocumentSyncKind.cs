namespace Csls.Protocol;

/// <summary>
/// Specifies how text document content changes are synchronized.
/// </summary>
public enum TextDocumentSyncKind
{
    /// <summary>
    /// Disables document synchronization.
    /// </summary>
    None = 0,

    /// <summary>
    /// Sends the complete document for every change.
    /// </summary>
    Full = 1,

    /// <summary>
    /// Sends incremental document edits.
    /// </summary>
    Incremental = 2
}
