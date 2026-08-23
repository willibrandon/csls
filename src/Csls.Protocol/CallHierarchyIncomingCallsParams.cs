namespace Csls.Protocol;

/// <summary>
/// Identifies the call-hierarchy item whose callers should be returned.
/// </summary>
public sealed record CallHierarchyIncomingCallsParams
{
    /// <summary>
    /// Gets the prepared callable item.
    /// </summary>
    public required CallHierarchyItem Item { get; init; }
}
