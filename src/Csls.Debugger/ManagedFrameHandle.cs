using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one runtime frame pointer and its stop-generation identity.
/// </summary>
internal sealed class ManagedFrameHandle
{
    /// <summary>
    /// Gets or initializes the session-local DAP frame identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets or initializes the stop generation that owns the frame.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugFrame pointer.
    /// </summary>
    internal required nint Pointer { get; init; }
}
