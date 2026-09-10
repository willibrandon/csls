using Microsoft.Win32.SafeHandles;

namespace Csls.Debugger;

/// <summary>
/// Owns a runtime-startup callback registration returned by dbgshim.
/// </summary>
internal sealed class DbgShimRegistrationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates an owned wrapper for a non-null dbgshim registration token.
    /// </summary>
    /// <param name="value">The native registration token.</param>
    internal DbgShimRegistrationHandle(nint value)
        : base(ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        SetHandle(value);
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() =>
        DbgShimNativeMethods.UnregisterForRuntimeStartup(handle) >= 0;
}
