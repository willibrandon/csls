namespace Csls.Protocol;

/// <summary>
/// Identifies the call-hierarchy item whose callees should be returned.
/// </summary>
public sealed record CallHierarchyOutgoingCallsParams
{
    /// <summary>
    /// Gets the prepared callable item.
    /// </summary>
    public required CallHierarchyItem Item { get; init; }
}
