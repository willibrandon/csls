using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Csls.Debugger.Tests;

/// <summary>
/// Represents the process identity in a native Restart Manager result.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DebuggerRestartManagerProcess
{
    /// <summary>
    /// Contains the operating-system process identifier.
    /// </summary>
    internal uint _processId;

    /// <summary>
    /// Contains the process creation time used to distinguish reused identifiers.
    /// </summary>
    internal FILETIME _startTime;
}
