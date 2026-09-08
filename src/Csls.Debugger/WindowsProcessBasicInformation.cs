using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Matches the public PROCESS_BASIC_INFORMATION layout for an owned Windows process query.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsProcessBasicInformation
{
    /// <summary>
    /// Stores the first reserved pointer-sized value.
    /// </summary>
    internal nint _reserved1;

    /// <summary>
    /// Stores the process environment block address.
    /// </summary>
    internal nint _pebBaseAddress;

    /// <summary>
    /// Stores the second reserved pointer-sized value.
    /// </summary>
    internal nint _reserved2;

    /// <summary>
    /// Stores the third reserved pointer-sized value.
    /// </summary>
    internal nint _reserved3;

    /// <summary>
    /// Stores the selected process identifier.
    /// </summary>
    internal nuint _processId;

    /// <summary>
    /// Stores the creating process identifier retained by Windows.
    /// </summary>
    internal nuint _parentProcessId;
}
