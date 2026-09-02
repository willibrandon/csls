namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies a source-level managed stepping operation.
/// </summary>
public enum DebugStepKind
{
    /// <summary>
    /// Steps into a called managed function when source is available.
    /// </summary>
    Into,

    /// <summary>
    /// Steps over calls and stops at the next source position in the current frame.
    /// </summary>
    Over,

    /// <summary>
    /// Runs until the current managed frame returns.
    /// </summary>
    Out
}
