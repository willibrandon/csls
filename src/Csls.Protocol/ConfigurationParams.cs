namespace Csls.Protocol;

/// <summary>
/// Contains the ordered configuration sections requested from an LSP client.
/// </summary>
public sealed record ConfigurationParams
{
    /// <summary>
    /// Gets the ordered configuration section requests.
    /// </summary>
    public required IReadOnlyList<ConfigurationItem> Items { get; init; }
}
