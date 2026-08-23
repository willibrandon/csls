namespace Csls.Protocol;

/// <summary>
/// Identifies the type-hierarchy item whose direct supertypes should be returned.
/// </summary>
public sealed record TypeHierarchySupertypesParams
{
    /// <summary>
    /// Gets the prepared type item.
    /// </summary>
    public required TypeHierarchyItem Item { get; init; }
}
