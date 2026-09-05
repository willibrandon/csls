namespace Csls.Debugger;

/// <summary>
/// Anchors one opaque managed-IL reference to a retained stopped frame and IL offset.
/// </summary>
internal sealed class ManagedInstructionReferenceHandle
{
    /// <summary>
    /// Gets or initializes the retained frame that owns the method body.
    /// </summary>
    internal required ManagedFrameHandle Frame { get; init; }

    /// <summary>
    /// Gets or initializes the reference's zero-based method-body IL offset.
    /// </summary>
    internal required uint IlOffset { get; init; }
}
