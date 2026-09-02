namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the lifecycle state of a debugger session.
/// </summary>
public enum DebugSessionState
{
    /// <summary>
    /// The session does not own a target.
    /// </summary>
    Created,

    /// <summary>
    /// The session is activating a target runtime.
    /// </summary>
    Starting,

    /// <summary>
    /// The target is running.
    /// </summary>
    Running,

    /// <summary>
    /// The target is stopped for debugger inspection.
    /// </summary>
    Stopped,

    /// <summary>
    /// The session is terminating or detaching from its target.
    /// </summary>
    Terminating,

    /// <summary>
    /// The session ended normally.
    /// </summary>
    Terminated,

    /// <summary>
    /// The session ended because of an unrecoverable engine failure.
    /// </summary>
    Faulted
}
