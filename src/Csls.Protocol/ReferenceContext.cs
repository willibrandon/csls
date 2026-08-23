namespace Csls.Protocol;

/// <summary>
/// Configures declaration inclusion for one reference search.
/// </summary>
public sealed record ReferenceContext
{
    /// <summary>
    /// Gets whether symbol declaration locations are included.
    /// </summary>
    public bool IncludeDeclaration { get; init; }
}
