namespace Csls.Debugger;

/// <summary>
/// Identifies one runtime-backed frame variable collection.
/// </summary>
internal enum ManagedScopeKind
{
    /// <summary>
    /// Method receiver and argument values.
    /// </summary>
    Arguments,

    /// <summary>
    /// Lexically active local values.
    /// </summary>
    Locals
}
