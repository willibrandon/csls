namespace Csls.Protocol;

/// <summary>
/// Preserves immutable source coordinates required to expand a hierarchy item.
/// </summary>
public sealed record HierarchyItemData
{
    /// <summary>
    /// Gets the workspace generation that produced the item.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Gets the source document containing the declaration.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the declaration identifier position used to recover its symbol.
    /// </summary>
    public required Position Position { get; init; }
}
