namespace Csls.Debugger;

/// <summary>
/// Owns one temporary callee breakpoint for a source-aware Step Into operation.
/// </summary>
internal sealed class ManagedTargetBreakpoint
{
    /// <summary>
    /// Gets or initializes the owned ICorDebugFunctionBreakpoint pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets or initializes the owned canonical COM identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }

    /// <summary>
    /// Gets or initializes the selected managed thread identifier.
    /// </summary>
    internal required int ThreadId { get; init; }

    /// <summary>
    /// Gets or sets how many earlier invocations of the same callee remain.
    /// </summary>
    internal int HitsToSkip { get; set; }
}
