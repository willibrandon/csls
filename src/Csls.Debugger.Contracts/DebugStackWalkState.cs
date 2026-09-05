namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the current or final state of one managed stack inspection.
/// </summary>
public enum DebugStackWalkState
{
    /// <summary>
    /// Native stack traversal is still active.
    /// </summary>
    Walking,

    /// <summary>
    /// The requested page or stack end was reached successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Cancellation ended traversal and released unpublished frame bindings.
    /// </summary>
    Canceled,

    /// <summary>
    /// Inspection failed and released unpublished frame bindings.
    /// </summary>
    Failed
}
