namespace Csls.Protocol;

/// <summary>
/// Controls how a workspace delete operation handles directories and missing resources.
/// </summary>
public sealed record DeleteFileOptions
{
    /// <summary>
    /// Gets whether a missing resource should leave the operation successful and unchanged.
    /// </summary>
    public bool? IgnoreIfNotExists { get; init; }

    /// <summary>
    /// Gets whether a directory and all of its descendants may be deleted.
    /// </summary>
    public bool? Recursive { get; init; }
}
