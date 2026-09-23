namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines private debugger-control notification method names.
/// </summary>
public static class DebuggerControlNotifications
{
    /// <summary>
    /// Signals that one or more authoritative debugger resources changed.
    /// </summary>
    public const string ResourceChanged = "debugger/resourceChanged";
}
