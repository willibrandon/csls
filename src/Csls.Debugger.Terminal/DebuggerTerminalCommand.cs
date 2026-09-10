namespace Csls.Debugger.Terminal;

/// <summary>
/// Identifies an operation available from the debugger terminal command palette.
/// </summary>
internal enum DebuggerTerminalCommand
{
    /// <summary>
    /// Adds a side-effect-free watch expression.
    /// </summary>
    AddWatch,

    /// <summary>
    /// Removes every configured watch expression.
    /// </summary>
    ClearWatches,

    /// <summary>
    /// Continues the stopped target.
    /// </summary>
    Continue,

    /// <summary>
    /// Pauses the running target.
    /// </summary>
    Pause,

    /// <summary>
    /// Steps over the current source statement.
    /// </summary>
    StepOver,

    /// <summary>
    /// Steps into the current source statement.
    /// </summary>
    StepInto,

    /// <summary>
    /// Steps out of the current managed frame.
    /// </summary>
    StepOut,

    /// <summary>
    /// Toggles a source breakpoint at the source cursor.
    /// </summary>
    ToggleBreakpoint,

    /// <summary>
    /// Restarts the activated target with its original request.
    /// </summary>
    Restart,

    /// <summary>
    /// Terminates the activated target.
    /// </summary>
    Terminate,

    /// <summary>
    /// Detaches the debugger without terminating the target.
    /// </summary>
    Detach
}
