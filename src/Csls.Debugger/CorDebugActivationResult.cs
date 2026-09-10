namespace Csls.Debugger;

/// <summary>
/// Carries the owned CoreCLR debugger and process interfaces created during runtime startup.
/// </summary>
internal readonly record struct CorDebugActivationResult
{
    /// <summary>
    /// Creates one successful runtime activation result with transferred COM ownership.
    /// </summary>
    /// <param name="corDebug">The owned ICorDebug interface pointer.</param>
    /// <param name="process">The owned ICorDebugProcess interface pointer.</param>
    internal CorDebugActivationResult(nint corDebug, nint process)
    {
        ArgumentOutOfRangeException.ThrowIfZero(corDebug);
        ArgumentOutOfRangeException.ThrowIfZero(process);
        CorDebug = corDebug;
        Process = process;
    }

    /// <summary>
    /// Gets the owned ICorDebug interface pointer.
    /// </summary>
    internal nint CorDebug { get; }

    /// <summary>
    /// Gets the owned ICorDebugProcess interface pointer.
    /// </summary>
    internal nint Process { get; }
}
