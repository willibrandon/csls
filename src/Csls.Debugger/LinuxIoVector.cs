using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Describes a Linux register buffer whose native byte count is updated by ptrace.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LinuxIoVector
{
    /// <summary>
    /// Gets or sets the address of the caller-owned register buffer.
    /// </summary>
    internal nint _address;

    /// <summary>
    /// Gets or sets the native buffer capacity or returned byte count.
    /// </summary>
    internal nuint _length;
}
