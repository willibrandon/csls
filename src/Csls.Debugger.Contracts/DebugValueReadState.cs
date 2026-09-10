namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the current or final state of a live variable inspection.
/// </summary>
public enum DebugValueReadState
{
    /// <summary>
    /// Runtime value inspection is active.
    /// </summary>
    Reading,

    /// <summary>
    /// The requested page is ready for publication.
    /// </summary>
    Completed,

    /// <summary>
    /// Cancellation released the operation's unpublished values.
    /// </summary>
    Canceled,

    /// <summary>
    /// Inspection failed and released the operation's unpublished values.
    /// </summary>
    Failed
}
