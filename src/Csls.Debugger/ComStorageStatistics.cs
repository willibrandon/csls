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
    internal nint Name;

    /// <summary>
    /// Receives the storage object type.
    /// </summary>
    internal uint Type;

    /// <summary>
    /// Receives the stream size in bytes.
    /// </summary>
    internal ulong Size;

    /// <summary>
    /// Receives the last modification timestamp.
    /// </summary>
    internal long ModifiedTime;

    /// <summary>
    /// Receives the creation timestamp.
    /// </summary>
    internal long CreationTime;

    /// <summary>
    /// Receives the last access timestamp.
    /// </summary>
    internal long AccessTime;

    /// <summary>
    /// Receives the storage mode flags.
    /// </summary>
    internal uint Mode;

    /// <summary>
    /// Receives the supported locking flags.
    /// </summary>
    internal uint SupportedLocks;

    /// <summary>
    /// Receives the storage class identifier.
    /// </summary>
    internal Guid ClassId;

    /// <summary>
    /// Receives the storage state bits.
    /// </summary>
    internal uint StateBits;

    /// <summary>
    /// Receives the reserved storage field.
    /// </summary>
    internal uint Reserved;
}
