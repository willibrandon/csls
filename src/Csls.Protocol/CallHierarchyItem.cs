namespace Csls.Protocol;

/// <summary>
/// Describes one callable source declaration in a call hierarchy.
/// </summary>
public sealed record CallHierarchyItem
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
    /// Gets a human-readable declaration signature.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets the source document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the complete declaration range.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the range selected when revealing the declaration.
    /// </summary>
    public required Range SelectionRange { get; init; }

    /// <summary>
    /// Gets source coordinates preserved for hierarchy expansion.
    /// </summary>
    public HierarchyItemData? Data { get; init; }
}
