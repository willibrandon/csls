namespace Csls.Protocol;

/// <summary>
/// Advertises server support for workspace folders and change notifications.
/// </summary>
public sealed record WorkspaceFoldersServerCapabilities
{
    /// <summary>
    /// Gets whether the server supports multiple workspace folders.
    /// </summary>
    public bool Supported { get; init; }

    /// <summary>
    /// Gets whether the client should send workspace-folder change notifications.
    /// </summary>
    public bool ChangeNotifications { get; init; }
}
