namespace Csls.Protocol;

/// <summary>
/// Describes one callee and its call sites within the requesting caller.
/// </summary>
public sealed record CallHierarchyOutgoingCall
{
    /// <summary>
    /// Gets the declaration being called.
    /// </summary>
    public required CallHierarchyItem To { get; init; }

    /// <summary>
    /// Gets call-site ranges relative to the caller document.
    /// </summary>
    public required IReadOnlyList<Range> FromRanges { get; init; }
}
