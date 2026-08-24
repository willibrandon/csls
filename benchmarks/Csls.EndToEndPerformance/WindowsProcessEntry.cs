using System.Runtime.InteropServices;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Holds the Windows Tool Help process fields needed for parent-child traversal.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WindowsProcessEntry
{
    /// <summary>
    /// Holds the marshalled native structure size.
    /// </summary>
    internal uint _size;

    /// <summary>
    /// Holds the native reference count retained for structure compatibility.
    /// </summary>
    internal uint _usage;

    /// <summary>
    /// Holds the native process identifier.
    /// </summary>
    internal uint _processId;

    /// <summary>
    /// Holds the default heap identifier retained for structure compatibility.
    /// </summary>
    internal UIntPtr _defaultHeapId;

    /// <summary>
    /// Holds the module identifier retained for structure compatibility.
    /// </summary>
    internal uint _moduleId;

    /// <summary>
    /// Holds the process thread count retained for structure compatibility.
    /// </summary>
    internal uint _threads;

    /// <summary>
    /// Holds the native parent process identifier.
    /// </summary>
    internal uint _parentProcessId;

    /// <summary>
    /// Holds the base priority retained for structure compatibility.
    /// </summary>
    internal int _basePriority;

    /// <summary>
    /// Holds the native flags retained for structure compatibility.
    /// </summary>
    internal uint _flags;

    /// <summary>
    /// Holds the executable name retained for structure compatibility.
    /// </summary>
    internal fixed char _executableFile[260];
}
