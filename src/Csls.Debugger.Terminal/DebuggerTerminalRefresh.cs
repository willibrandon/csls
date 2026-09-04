using Hex1b;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Coalesces debugger publications until the application begins reading its next frame.
/// </summary>
internal sealed class DebuggerTerminalRefresh
{
    private static readonly DebuggerTerminalRefreshEvent s_refreshEvent = new();
    private readonly Hex1bAppWorkloadAdapter _workload;
    private int _pending;

    /// <summary>
    /// Connects debugger refresh notifications to the application's existing event queue.
    /// </summary>
    /// <param name="workload">The real workload adapter owned by the terminal.</param>
    internal DebuggerTerminalRefresh(Hex1bAppWorkloadAdapter workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        _workload = workload;
    }

    /// <summary>
    /// Queues one presentation notification for all publications not yet observed by a frame.
    /// </summary>
    internal void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0 &&
            !_workload.TryWriteInputEvent(s_refreshEvent))
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    /// <summary>
    /// Rearms notifications before the application reads its published display snapshot.
    /// </summary>
    internal void Acknowledge() => Volatile.Write(ref _pending, 0);
}
