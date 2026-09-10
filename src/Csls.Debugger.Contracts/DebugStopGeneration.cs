namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the target stop that owns debugger inspection handles.
/// </summary>
/// <param name="Value">The monotonically increasing stop number.</param>
public readonly record struct DebugStopGeneration(long Value)
{
    /// <summary>
    /// Gets the first valid stopped generation.
    /// </summary>
    public static DebugStopGeneration First => new(1);

    /// <summary>
    /// Creates the generation that follows this generation.
    /// </summary>
    /// <returns>The next debugger stop generation.</returns>
    public DebugStopGeneration Next() => checked(new(Value + 1));
}
