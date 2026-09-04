namespace Csls.Debugger;

/// <summary>
/// Tracks bounded logical and physical progress during one object expansion.
/// </summary>
internal sealed class ManagedObjectExpansionState
{
    /// <summary>
    /// Gets or sets the number of physical fields inspected across flattened objects.
    /// </summary>
    internal int PhysicalFieldCount { get; set; }

    /// <summary>
    /// Gets or sets the zero-based logical child position.
    /// </summary>
    internal int VisibleIndex { get; set; }

    /// <summary>
    /// Gets or sets whether debugger metadata transformed the default view.
    /// </summary>
    internal bool WasTransformed { get; set; }
}
