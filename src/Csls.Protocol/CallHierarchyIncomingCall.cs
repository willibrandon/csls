namespace Csls.Protocol;

/// <summary>
/// Describes one caller and its call sites within that caller.
/// </summary>
public sealed record CallHierarchyIncomingCall
{
    /// <summary>
    /// Gets the declaration that makes the call.
    /// </summary>
    public required CallHierarchyItem From { get; init; }

    /// <summary>
    /// Gets call-site ranges relative to the caller document.
    /// </summary>
    public required IReadOnlyList<Range> FromRanges { get; init; }
}
