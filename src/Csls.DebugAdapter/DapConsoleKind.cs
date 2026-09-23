namespace Csls.DebugAdapter;

/// <summary>
/// Selects the client-owned or adapter-owned console for a launched target.
/// </summary>
internal enum DapConsoleKind
{
    /// <summary>
    /// Routes target output through the adapter's DAP output events.
    /// </summary>
    Internal,

    /// <summary>
    /// Runs the target with a client-provided integrated terminal.
    /// </summary>
    Integrated,

    /// <summary>
    /// Runs the target with a client-provided external terminal.
    /// </summary>
    External
}
