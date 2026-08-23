namespace Csls.Protocol;

/// <summary>
/// Advertises workspace symbol search and lazy location resolution behavior.
/// </summary>
public sealed record WorkspaceSymbolOptions
{
    /// <summary>
    /// Gets whether workspace symbol results can be resolved.
    /// </summary>
    public bool ResolveProvider { get; init; }
}
