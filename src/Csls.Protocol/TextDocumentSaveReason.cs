namespace Csls.Protocol;

/// <summary>
/// Identifies why an editor is saving a text document.
/// </summary>
public enum TextDocumentSaveReason
{
    /// <summary>
    /// Indicates no valid LSP save reason.
    /// </summary>
    None = 0,

    /// <summary>
    /// The user or an API explicitly requested the save.
    /// </summary>
    Manual = 1,

    /// <summary>
    /// The editor automatically saved after a delay.
    /// </summary>
    AfterDelay = 2,

    /// <summary>
    /// The editor saved because the document lost focus.
    /// </summary>
    FocusOut = 3
}
