namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the managed exception stage that caused a debugger stop.
/// </summary>
public enum DebugExceptionBreakMode
{
    /// <summary>
    /// The debugger stopped when the exception was thrown.
    /// </summary>
    Thrown,

    /// <summary>
    /// The exception escaped user code without a user-code handler.
    /// </summary>
    UserUnhandled,

    /// <summary>
    /// The runtime found no handler for the exception.
    /// </summary>
    Unhandled
}
