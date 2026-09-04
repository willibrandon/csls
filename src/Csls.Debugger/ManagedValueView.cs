namespace Csls.Debugger;

/// <summary>
/// Selects the debugger presentation applied while expanding a retained value.
/// </summary>
internal enum ManagedValueView
{
    /// <summary>
    /// Applies debugger presentation metadata from the target type.
    /// </summary>
    Default,

    /// <summary>
    /// Exposes the target's physical runtime fields without presentation metadata.
    /// </summary>
    Raw
}
