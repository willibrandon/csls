using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Projects the public macOS VM-region short-info layout without per-page accounting.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 52)]
internal struct DebuggerMacRegionInfo
{
    /// <summary>
    /// Gets the region's active read, write, and execute permissions.
    /// </summary>
    [FieldOffset(0)]
    internal uint Protection;

    /// <summary>
    /// Gets the region's maximum read, write, and execute permissions.
    /// </summary>
    [FieldOffset(4)]
    internal uint MaxProtection;

    /// <summary>
    /// Gets the operating system's memory-purpose tag.
    /// </summary>
    [FieldOffset(24)]
    internal uint UserTag;

    /// <summary>
    /// Gets the region's private, shared, or copy-on-write mode.
    /// </summary>
    [FieldOffset(35)]
    internal byte ShareMode;

    /// <summary>
    /// Gets whether this entry contains nested mappings.
    /// </summary>
    [FieldOffset(36)]
    internal int IsSubmap;
}
