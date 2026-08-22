namespace Csls.Server;

/// <summary>
/// Represents the ordered LSP lifecycle states accepted by the server.
/// </summary>
public enum ServerLifecycleState
{
    /// <summary>
    /// The server is waiting for initialize.
    /// </summary>
    Created,

    /// <summary>
    /// The server returned initialize and is waiting for initialized.
    /// </summary>
    InitializeResponded,

    /// <summary>
    /// The server accepts ordinary LSP requests and notifications.
    /// </summary>
    Running,

    /// <summary>
    /// The server received the shutdown request.
    /// </summary>
    ShuttingDown,

    /// <summary>
    /// The server received the exit notification.
    /// </summary>
    Exited
}
