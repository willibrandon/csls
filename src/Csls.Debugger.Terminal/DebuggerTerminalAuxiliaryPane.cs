namespace Csls.Debugger.Terminal;

/// <summary>
/// Selects the bounded auxiliary debugger view shown in the terminal.
/// </summary>
internal enum DebuggerTerminalAuxiliaryPane
{
    /// <summary>
    /// Shows retained target standard output and standard error.
    /// </summary>
    Output,

    /// <summary>
    /// Shows loaded managed modules and their symbol state.
    /// </summary>
    Modules,

    /// <summary>
    /// Shows the authoritative configured breakpoint sets.
    /// </summary>
    Breakpoints,

    /// <summary>
    /// Shows side-effect-free expressions evaluated in the selected frame.
    /// </summary>
    Watches,

    /// <summary>
    /// Shows the managed exception responsible for the current stop.
    /// </summary>
    Exception
}
