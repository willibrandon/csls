namespace Csls.Debugger.Contracts;

/// <summary>
/// Signals that authoritative debugger resources should be read again.
/// </summary>
public sealed class DebuggerResourceChangeEventArgs : EventArgs
{
    /// <summary>
    /// Gets the resource groups invalidated by the notification.
    /// </summary>
    public required DebuggerResourceChangeKind Kind { get; init; }
}
