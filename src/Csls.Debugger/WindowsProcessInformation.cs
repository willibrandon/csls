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
    internal nint _processHandle;

    /// <summary>
    /// Gets or sets the owned initial-thread handle.
    /// </summary>
    internal nint _threadHandle;

    /// <summary>
    /// Gets or sets the operating-system process identifier.
    /// </summary>
    internal uint _processId;

    /// <summary>
    /// Gets or sets the operating-system initial-thread identifier.
    /// </summary>
    internal uint _threadId;
}
