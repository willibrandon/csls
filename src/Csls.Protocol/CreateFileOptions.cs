namespace Csls.Protocol;

/// <summary>
/// Controls how a workspace create operation handles an existing target resource.
/// </summary>
public sealed record CreateFileOptions
{
    /// <summary>
    /// Gets whether an existing target may be overwritten.
    /// </summary>
    public bool? Overwrite { get; init; }

    /// <summary>
    /// Gets whether an existing target should leave the operation successful and unchanged.
    /// </summary>
    public bool? IgnoreIfExists { get; init; }
}
