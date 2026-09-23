using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Projects the four-byte-packed macOS VM-region short-info layout without per-page accounting.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 48, Pack = 4)]
internal struct DebuggerMacRegionInfo
{
    /// <summary>
    /// Stores the region's active read, write, and execute permissions.
    /// </summary>
    [FieldOffset(0)]
    internal uint _protection;

    /// <summary>
    /// Stores the region's maximum read, write, and execute permissions.
    /// </summary>
    [FieldOffset(4)]
    internal uint _maxProtection;

    /// <summary>
    /// Stores the operating system's memory-purpose tag.
    /// </summary>
    [FieldOffset(20)]
    internal uint _userTag;

    /// <summary>
    /// Stores the region's private, shared, or copy-on-write mode.
    /// </summary>
    [FieldOffset(31)]
    internal byte _shareMode;

    /// <summary>
    /// Stores whether this entry contains nested mappings.
    /// </summary>
    [FieldOffset(32)]
    internal int _isSubmap;
}
