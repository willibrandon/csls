using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Projects the native STATSTG layout used to obtain a COM stream's bounded size.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComStorageStatistics
{
    /// <summary>
    /// Receives an optional COM-allocated name that is suppressed by the caller.
    /// </summary>
    internal nint _name;

    /// <summary>
    /// Receives the storage object type.
    /// </summary>
    internal uint _type;

    /// <summary>
    /// Receives the stream size in bytes.
    /// </summary>
    internal ulong _size;

    /// <summary>
    /// Receives the last modification timestamp.
    /// </summary>
    internal long _modifiedTime;

    /// <summary>
    /// Receives the creation timestamp.
    /// </summary>
    internal long _creationTime;

    /// <summary>
    /// Receives the last access timestamp.
    /// </summary>
    internal long _accessTime;

    /// <summary>
    /// Receives the storage mode flags.
    /// </summary>
    internal uint _mode;

    /// <summary>
    /// Receives the supported locking flags.
    /// </summary>
    internal uint _supportedLocks;

    /// <summary>
    /// Receives the storage class identifier.
    /// </summary>
    internal Guid _classId;

    /// <summary>
    /// Receives the storage state bits.
    /// </summary>
    internal uint _stateBits;

    /// <summary>
    /// Receives the reserved storage field.
    /// </summary>
    internal uint _reserved;
}
