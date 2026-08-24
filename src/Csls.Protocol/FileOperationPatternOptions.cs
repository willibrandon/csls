namespace Csls.Protocol;

/// <summary>
/// Configures matching for one file-operation glob.
/// </summary>
public sealed record FileOperationPatternOptions
{
    /// <summary>
    /// Gets whether the client ignores casing while matching the glob.
    /// </summary>
    public bool IgnoreCase { get; init; }
}
