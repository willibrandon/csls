namespace Csls.Debugger;

/// <summary>
/// Owns a loaded module pointer and its canonical COM identity.
/// </summary>
internal sealed class CorDebugLoadedModule
{
    /// <summary>
    /// Gets the owned ICorDebugModule pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets the owned canonical IUnknown identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }
}
