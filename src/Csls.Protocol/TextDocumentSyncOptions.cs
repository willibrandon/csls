namespace Csls.Protocol;

/// <summary>
/// Describes the server's text document synchronization behavior.
/// </summary>
public sealed record TextDocumentSyncOptions
{
    /// <summary>
    /// Gets whether the client sends open and close notifications.
    /// </summary>
    public bool OpenClose { get; init; }

    /// <summary>
    /// Gets the content synchronization strategy.
    /// </summary>
    public TextDocumentSyncKind Change { get; init; }

    /// <summary>
    /// Gets whether the client sends save notifications.
    /// </summary>
    public bool Save { get; init; }

    /// <summary>
    /// Gets whether the server can return edits immediately before a save.
    /// </summary>
    public bool WillSaveWaitUntil { get; init; }
}
