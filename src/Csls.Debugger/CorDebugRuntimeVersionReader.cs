using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads the exact target CoreCLR version through the public debugger contract.
/// </summary>
internal static class CorDebugRuntimeVersionReader
{
    /// <summary>
    /// Reads the runtime product version associated with one debug process.
    /// </summary>
    /// <param name="process">The borrowed ICorDebugProcess pointer.</param>
    /// <returns>The runtime version, or null when the runtime omits ICorDebugProcess2.</returns>
    internal static unsafe Version? TryRead(nint process)
    {
        ArgumentOutOfRangeException.ThrowIfZero(process);
        if (!ComAbi.TryQueryInterface(
            process,
            ICorDebugProcess2Abi.InterfaceId,
            out nint process2))
        {
            return null;
        }

        try
        {
            uint* components = stackalloc uint[4];
            int result = new ICorDebugProcess2Abi(process2).GetVersion((nint)components);
            if (result < 0 || components[0] > int.MaxValue ||
                components[1] > int.MaxValue || components[2] > int.MaxValue ||
                components[3] > int.MaxValue)
            {
                return null;
            }

            return new Version(
                checked((int)components[0]),
                checked((int)components[1]),
                checked((int)components[2]),
                checked((int)components[3]));
        }
        finally
        {
            _ = ComAbi.Release(process2);
        }
    }
}
