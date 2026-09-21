using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Projects the public macOS VM-region short-info layout without per-page accounting.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 52)]
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
    [FieldOffset(24)]
    internal uint _userTag;

    /// <summary>
    /// Stores the region's private, shared, or copy-on-write mode.
    /// </summary>
    [FieldOffset(35)]
    internal byte _shareMode;

    /// <summary>
    /// Stores whether this entry contains nested mappings.
    /// </summary>
    [FieldOffset(36)]
    internal int _isSubmap;
}
