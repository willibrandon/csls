namespace Csls.Protocol;

/// <summary>
/// Identifies the type-hierarchy item whose direct subtypes should be returned.
/// </summary>
public sealed record TypeHierarchySubtypesParams
{
    /// <summary>
    /// Gets the prepared type item.
    /// </summary>
    public required TypeHierarchyItem Item { get; init; }
}
