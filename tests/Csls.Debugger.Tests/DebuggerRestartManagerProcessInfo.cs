using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Matches the fixed native Restart Manager application-information layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DebuggerRestartManagerProcessInfo
{
    /// <summary>
    /// Contains the process identifier and creation time.
    /// </summary>
    internal DebuggerRestartManagerProcess _process;

    /// <summary>
    /// Contains the fixed UTF-16 application name buffer.
    /// </summary>
    internal fixed ushort _applicationName[256];

    /// <summary>
    /// Contains the fixed UTF-16 service name buffer.
    /// </summary>
    internal fixed ushort _serviceName[64];

    /// <summary>
    /// Contains the native application classification.
    /// </summary>
    internal int _applicationType;

    /// <summary>
    /// Contains the native application status flags.
    /// </summary>
    internal uint _status;

    /// <summary>
    /// Contains the terminal-services session identifier.
    /// </summary>
    internal uint _sessionId;

    /// <summary>
    /// Contains the four-byte native restartability flag.
    /// </summary>
    internal int _restartable;
}
