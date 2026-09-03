using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Extends Windows startup state with an explicit inherited-handle allowlist.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsStartupInfoEx
{
    /// <summary>
    /// Gets or sets the base Windows startup state.
    /// </summary>
    internal WindowsStartupInfo _startupInfo;

    /// <summary>
    /// Gets or sets the initialized process-thread attribute list.
    /// </summary>
    internal nint _attributeList;
}
