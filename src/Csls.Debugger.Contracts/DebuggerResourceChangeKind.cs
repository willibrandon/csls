namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies debugger resources invalidated by one engine notification.
/// </summary>
[Flags]
public enum DebuggerResourceChangeKind
{
    /// <summary>
    /// The debugger lifecycle or stopped generation changed.
    /// </summary>
    Session = 1,

    /// <summary>
    /// New retained target output is available.
    /// </summary>
    Output = 2,

    /// <summary>
    /// Breakpoint bindings or managed-exception policies changed.
    /// </summary>
    Breakpoints = 4
}
