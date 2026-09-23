using System.Runtime.InteropServices;

namespace Csls.Debugger.Worker;

/// <summary>
/// Represents the eight-byte POSIX pollfd layout used by Linux and macOS input waits.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UnixInputPollDescriptor
{
    /// <summary>
    /// Identifies the borrowed descriptor protected by the owning stream's read gate.
    /// </summary>
    internal int _descriptor;

    /// <summary>
    /// Selects the requested POSIX readiness events.
    /// </summary>
    internal short _events;

    /// <summary>
    /// Receives the readiness events reported by poll.
    /// </summary>
    internal short _returnedEvents;
}
