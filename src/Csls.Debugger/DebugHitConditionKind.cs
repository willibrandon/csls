namespace Csls.Debugger;

/// <summary>
/// Identifies the supported managed breakpoint hit-count comparison.
/// </summary>
internal enum DebugHitConditionKind
{
    /// <summary>
    /// Stops only when the hit count equals the requested value.
    /// </summary>
    Equal,

    /// <summary>
    /// Stops when the hit count reaches or exceeds the requested value.
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Stops whenever the hit count is a multiple of the requested value.
    /// </summary>
    Multiple
}
