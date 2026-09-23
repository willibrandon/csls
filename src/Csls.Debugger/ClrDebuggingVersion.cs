using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Stores the five WORD fields of the public CLR_DEBUGGING_VERSION native contract.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ClrDebuggingVersion
{
    /// <summary>
    /// Identifies the initial version of this native structure.
    /// </summary>
    internal ushort _structVersion;

    /// <summary>
    /// Identifies the major CLR debugging compatibility version.
    /// </summary>
    internal ushort _major;

    /// <summary>
    /// Identifies the minor CLR debugging compatibility version.
    /// </summary>
    internal ushort _minor;

    /// <summary>
    /// Identifies the CLR debugging compatibility build.
    /// </summary>
    internal ushort _build;

    /// <summary>
    /// Identifies the CLR debugging compatibility revision.
    /// </summary>
    internal ushort _revision;
}
