namespace Csls.Protocol;

/// <summary>
/// Selects workspace file operations by URI scheme and path pattern.
/// </summary>
public sealed record FileOperationFilter
{
    /// <summary>
    /// Gets the URI scheme matched by this filter.
    /// </summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// Gets the path pattern matched by this filter.
    /// </summary>
    public required FileOperationPattern Pattern { get; init; }
}
