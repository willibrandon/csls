namespace Csls.Protocol;

/// <summary>
/// Describes file-system changes observed by the LSP client.
/// </summary>
public sealed record DidChangeWatchedFilesParams
{
    /// <summary>
    /// Gets the ordered changed files.
    /// </summary>
    public required IReadOnlyList<FileEvent> Changes { get; init; }
}
