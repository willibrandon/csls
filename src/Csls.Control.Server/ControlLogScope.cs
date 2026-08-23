namespace Csls.Control;

/// <summary>
/// Provides a shared no-op logging scope for the bounded control logger.
/// </summary>
internal sealed class ControlLogScope : IDisposable
{
    /// <summary>
    /// Gets the shared no-op logging scope.
    /// </summary>
    internal static ControlLogScope Instance { get; } = new();

    private ControlLogScope()
    {
    }

    /// <summary>
    /// Completes the no-op scope.
    /// </summary>
    public void Dispose()
    {
    }
}
