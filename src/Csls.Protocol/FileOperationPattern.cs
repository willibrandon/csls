namespace Csls.Protocol;

/// <summary>
/// Describes paths that trigger one workspace file operation.
/// </summary>
public sealed record FileOperationPattern
{
    /// <summary>
    /// Gets the LSP glob matched by the client.
    /// </summary>
    public required string Glob { get; init; }

    /// <summary>
    /// Gets whether the glob matches files, folders, or both when omitted.
    /// </summary>
    public string? Matches { get; init; }

    /// <summary>
    /// Gets the additional glob matching options.
    /// </summary>
    public FileOperationPatternOptions? Options { get; init; }
}
