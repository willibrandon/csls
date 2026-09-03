namespace Csls.Debugger;

/// <summary>
/// Identifies one side-effect-free managed expression access operation.
/// </summary>
internal enum ManagedExpressionSegmentKind
{
    /// <summary>
    /// Selects an instance field by its metadata name.
    /// </summary>
    Member,

    /// <summary>
    /// Selects an array element by its CLR indexes.
    /// </summary>
    ArrayIndex
}
