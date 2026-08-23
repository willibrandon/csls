namespace Csls.Protocol;

/// <summary>
/// Contains one ordered workspace-folder change notification.
/// </summary>
public sealed record DidChangeWorkspaceFoldersParams
{
    /// <summary>
    /// Gets the added and removed workspace folders.
    /// </summary>
    public required WorkspaceFoldersChangeEvent Event { get; init; }
}
