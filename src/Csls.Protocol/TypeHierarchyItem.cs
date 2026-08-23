namespace Csls.Protocol;

/// <summary>
/// Describes one source type declaration in a type hierarchy.
/// </summary>
public sealed record TypeHierarchyItem
{
    /// <summary>
    /// Gets the type name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type declaration category.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Gets the containing namespace and type detail.
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
