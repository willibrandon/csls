namespace Csls.Protocol;

/// <summary>
/// Describes one parameter within a callable signature label.
/// </summary>
public sealed record ParameterInformation
{
    /// <summary>
    /// Gets the parameter label as it appears in the complete signature.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets optional parameter documentation.
    /// </summary>
    public MarkupContent? Documentation { get; init; }
}
