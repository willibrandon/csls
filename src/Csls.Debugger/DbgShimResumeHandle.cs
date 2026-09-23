using Microsoft.Win32.SafeHandles;

namespace Csls.Debugger;

/// <summary>
/// Owns the platform resume handle returned by dbgshim for a suspended launch.
/// </summary>
internal sealed class DbgShimResumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates an owned wrapper for a non-null dbgshim resume handle.
    /// </summary>
    /// <param name="value">The native resume handle.</param>
    internal DbgShimResumeHandle(nint value)
        : base(ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        SetHandle(value);
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => DbgShimNativeMethods.CloseResumeHandle(handle) >= 0;
}
