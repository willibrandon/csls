namespace Csls.Protocol;

/// <summary>
/// Selects the file-system changes requested from an LSP client.
/// </summary>
[Flags]
public enum WatchKind
{
    /// <summary>
    /// Requests no file-system change notifications.
    /// </summary>
    None = 0,

    /// <summary>
    /// Requests file creation notifications.
    /// </summary>
    Create = 1,

    /// <summary>
    /// Requests file content change notifications.
    /// </summary>
    Change = 2,

    /// <summary>
    /// Requests file deletion notifications.
    /// </summary>
    Delete = 4
}
