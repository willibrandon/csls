namespace Csls.Protocol;

/// <summary>
/// Carries files and folders created through the client.
/// </summary>
public sealed record CreateFilesParams
{
    /// <summary>
    /// Gets the created files and folders.
    /// </summary>
    public required IReadOnlyList<FileCreate> Files { get; init; }
}
