namespace Csls.Debugger;

/// <summary>
/// Identifies the logical breakpoint category that caused a runtime stop.
/// </summary>
internal enum DebugBreakpointKind
{
    /// <summary>
    /// Identifies a source-location breakpoint.
    /// </summary>
    Source,

    /// <summary>
    /// Identifies a managed function-entry breakpoint.
    /// </summary>
    Function
}
