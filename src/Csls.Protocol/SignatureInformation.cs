namespace Csls.Protocol;

/// <summary>
/// Describes one callable overload and its ordered parameters.
/// </summary>
public sealed record SignatureInformation
{
    /// <summary>
    /// Gets the complete callable signature label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets optional callable documentation.
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>
    /// Gets the ordered callable parameters.
    /// </summary>
    public IReadOnlyList<ParameterInformation>? Parameters { get; init; }

    /// <summary>
    /// Gets the active parameter for this overload when it differs from the global value.
    /// </summary>
    public int? ActiveParameter { get; init; }
}
