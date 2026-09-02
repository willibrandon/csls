namespace Csls.Debugger;

/// <summary>
/// Owns a loaded module pointer and its canonical COM identity.
/// </summary>
internal sealed class CorDebugLoadedModule
{
    /// <summary>
    /// Gets the stable session-local module identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the absolute module path when the runtime exposes one.
    /// </summary>
    internal required string? Path { get; init; }

    /// <summary>
    /// Gets the owned ICorDebugModule pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets the owned canonical IUnknown identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }
}
