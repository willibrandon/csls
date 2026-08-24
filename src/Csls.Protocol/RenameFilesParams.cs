namespace Csls.Protocol;

/// <summary>
/// Carries files and folders renamed through the client.
/// </summary>
public sealed record RenameFilesParams
{
    /// <summary>
    /// Gets the renamed files and folders.
    /// </summary>
    public required IReadOnlyList<FileRename> Files { get; init; }
}
