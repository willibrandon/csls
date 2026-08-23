namespace Csls.Protocol;

/// <summary>
/// Identifies one source URI and exact UTF-16 range.
/// </summary>
public sealed record Location
{
    /// <summary>
    /// Gets the source document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the exact source range.
    /// </summary>
    public required Range Range { get; init; }
}
