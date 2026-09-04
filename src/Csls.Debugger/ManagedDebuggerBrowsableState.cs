namespace Csls.Debugger;

/// <summary>
/// Identifies the debugger expansion policy encoded by DebuggerBrowsableAttribute.
/// </summary>
internal enum ManagedDebuggerBrowsableState
{
    /// <summary>
    /// Omits the attributed member from the default debugger view.
    /// </summary>
    Never,

    /// <summary>
    /// Presents the attributed member as an ordinary collapsed value.
    /// </summary>
    Collapsed,

    /// <summary>
    /// Replaces the attributed member with its immediate children.
    /// </summary>
    RootHidden
}
