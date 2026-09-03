using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Receives the handles and identifiers for a newly created Windows process.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsProcessInformation
{
    /// <summary>
    /// Gets or sets the owned process handle.
    /// </summary>
    internal nint ProcessHandle;

    /// <summary>
    /// Gets or sets the owned initial-thread handle.
    /// </summary>
    internal nint ThreadHandle;

    /// <summary>
    /// Gets or sets the operating-system process identifier.
    /// </summary>
    internal uint ProcessId;

    /// <summary>
    /// Gets or sets the operating-system initial-thread identifier.
    /// </summary>
    internal uint ThreadId;
}
