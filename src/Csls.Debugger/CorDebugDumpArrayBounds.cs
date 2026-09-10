namespace Csls.Debugger;

/// <summary>
/// Describes the lengths and lower bounds of a captured managed array.
/// </summary>
/// <param name="Lengths">The element count in each dimension.</param>
/// <param name="LowerBounds">The first valid index in each dimension.</param>
public sealed record CorDebugDumpArrayBounds(IReadOnlyList<int> Lengths, IReadOnlyList<int> LowerBounds);
