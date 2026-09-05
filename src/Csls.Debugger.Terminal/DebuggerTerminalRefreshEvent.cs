using Hex1b.Input;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Schedules presentation of a newly published debugger frame on the application event queue.
/// </summary>
internal sealed record DebuggerTerminalRefreshEvent : Hex1bEvent;
