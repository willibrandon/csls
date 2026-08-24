namespace Csls.Protocol;

/// <summary>
/// Controls how a workspace rename operation handles an existing destination resource.
/// </summary>
public sealed record RenameFileOptions
{
    /// <summary>
    /// Gets whether an existing destination may be overwritten.
    /// </summary>
    public bool? Overwrite { get; init; }

    /// <summary>
    /// Gets whether an existing destination should leave the operation successful and unchanged.
    /// </summary>
    public bool? IgnoreIfExists { get; init; }
}
