namespace Csls.Protocol;

/// <summary>
/// Carries files and folders deleted through the client.
/// </summary>
public sealed record DeleteFilesParams
{
    /// <summary>
    /// Gets the deleted files and folders.
    /// </summary>
    public required IReadOnlyList<FileDelete> Files { get; init; }
}
