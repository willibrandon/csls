namespace Csls.Protocol;

/// <summary>
/// Describes one source declaration returned by workspace symbol search.
/// </summary>
public sealed record WorkspaceSymbol
{
    /// <summary>
    /// Gets the declaration name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the declaration category.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Gets the containing declaration display name.
    /// </summary>
    public string? ContainerName { get; init; }

    /// <summary>
    /// Gets the source URI and optional resolved range.
    /// </summary>
    public required WorkspaceSymbolLocation Location { get; init; }

    /// <summary>
    /// Gets opaque coordinates preserved for a later resolve request.
    /// </summary>
    public WorkspaceSymbolData? Data { get; init; }
}
