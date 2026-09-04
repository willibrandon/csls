namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies how a debugger client should present one variable entry.
/// </summary>
public enum DebugVariablePresentationKind
{
    /// <summary>
    /// Presents an ordinary runtime-backed variable.
    /// </summary>
    Normal,

    /// <summary>
    /// Presents a debugger-created virtual container.
    /// </summary>
    Virtual
}
