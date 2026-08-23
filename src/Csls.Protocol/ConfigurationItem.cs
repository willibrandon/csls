namespace Csls.Protocol;

/// <summary>
/// Identifies one client configuration section and its optional resource scope.
/// </summary>
public sealed record ConfigurationItem
{
    /// <summary>
    /// Gets the resource URI used to resolve scoped configuration.
    /// </summary>
    public DocumentUri? ScopeUri { get; init; }

    /// <summary>
    /// Gets the configuration section requested from the client.
    /// </summary>
    public string? Section { get; init; }
}
