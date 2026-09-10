using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Matches the public PROCESSENTRY32W layout on Windows x86, x64, and arm64.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WindowsProcessSnapshotEntry
{
    /// <summary>
    /// Stores the size supplied to the snapshot API.
    /// </summary>
    internal uint _size;

    /// <summary>
    /// Stores the reserved usage count.
    /// </summary>
    internal uint _usageCount;

    /// <summary>
    /// Stores the process identifier.
    /// </summary>
    internal uint _processId;

    /// <summary>
    /// Stores the pointer-sized default heap identifier.
    /// </summary>
    internal nuint _defaultHeapId;

    /// <summary>
    /// Stores the reserved module identifier.
    /// </summary>
    internal uint _moduleId;

    /// <summary>
    /// Stores the process thread count.
    /// </summary>
    internal uint _threadCount;

    /// <summary>
    /// Stores the creating process identifier.
    /// </summary>
    internal uint _parentProcessId;

    /// <summary>
    /// Stores the signed base priority class.
    /// </summary>
    internal int _basePriority;

    /// <summary>
    /// Stores the reserved flags.
    /// </summary>
    internal uint _flags;

    /// <summary>
    /// Stores the native UTF-16 executable name buffer.
    /// </summary>
    internal fixed char _executableName[260];
}
