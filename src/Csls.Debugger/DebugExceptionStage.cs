namespace Csls.Debugger;

/// <summary>
/// Identifies a CoreCLR managed exception callback stage.
/// </summary>
internal enum DebugExceptionStage
{
    /// <summary>
    /// The exception was first thrown.
    /// </summary>
    Thrown,

    /// <summary>
    /// The exception first crossed out of user code.
    /// </summary>
    UserUnhandled,

    /// <summary>
    /// The exception has no runtime handler.
    /// </summary>
    Unhandled
}
