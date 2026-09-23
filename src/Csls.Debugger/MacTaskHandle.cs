using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Owns a Mach task send right acquired for native thread inspection.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacTaskHandle : SafeHandle
{
    /// <summary>
    /// Creates an empty owner before acquiring a native task right.
    /// </summary>
    internal MacTaskHandle() : base(0, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == 0 || handle == unchecked((nint)uint.MaxValue);

    /// <summary>
    /// Acquires a task port using the operating system's debugger authorization policy.
    /// </summary>
    /// <param name="processId">The target process identifier.</param>
    /// <returns>The owned task right.</returns>
    internal static MacTaskHandle Open(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        var handle = new MacTaskHandle();
        try
        {
            int result = MacThreadContextNativeMethods.OpenTask(
                MacThreadContextNativeMethods.GetCurrentTask(), processId, out uint port);
            handle.SetHandle(checked((nint)port));
            if (result != 0 || handle.IsInvalid)
            {
                throw new InvalidOperationException($"Native register inspection could not acquire task {processId}: Mach status 0x{result:X}.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => MacThreadContextNativeMethods.ReleasePort(
        MacThreadContextNativeMethods.GetCurrentTask(), checked((uint)handle)) == 0;
}
