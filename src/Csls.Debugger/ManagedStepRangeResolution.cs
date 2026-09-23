namespace Csls.Debugger;

/// <summary>
/// Carries the active IL position and its source stepping ranges together.
/// </summary>
internal readonly record struct ManagedStepRangeResolution
{
    /// <summary>
    /// Gets the active source statement and hidden compiler-code ranges.
    /// </summary>
    internal IReadOnlyList<ManagedStepRange> Ranges { get; init; }

    /// <summary>
    /// Gets whether the active instruction maps to compiler-generated code.
    /// </summary>
    internal bool CurrentIsHidden { get; init; }

    /// <summary>
    /// Gets the IL instruction offset at which the thread is stopped.
    /// </summary>
    internal uint CurrentIlOffset { get; init; }
}
