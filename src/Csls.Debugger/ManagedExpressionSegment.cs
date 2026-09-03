namespace Csls.Debugger;

/// <summary>
/// Describes one validated side-effect-free managed expression access.
/// </summary>
/// <param name="Kind">The runtime access operation.</param>
/// <param name="Name">The metadata member name or canonical array-index display.</param>
internal sealed record ManagedExpressionSegment(
    ManagedExpressionSegmentKind Kind,
    string Name);
