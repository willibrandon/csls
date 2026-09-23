using Csls.Debugger.Interop;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Discovers and validates the CoreCLR instance selected for local attachment.
/// </summary>
internal static class DbgShimRuntimeDiscovery
{
    /// <summary>
    /// Creates an owned IUnknown debugger interface for one already initialized runtime.
    /// </summary>
    /// <param name="processId">The target operating-system process identifier.</param>
    /// <param name="modulePath">The exact module path returned by runtime discovery.</param>
    /// <returns>The owned interface pointer, which the caller must release.</returns>
    internal static unsafe nint CreateDebuggingInterface(uint processId, string modulePath)
    {
        const int insufficientBuffer = unchecked((int)0x8007007A);
        const int corDebugVersion4 = 4;
        const uint maximumVersionLength = 1024;
        int sizeResult = DbgShimNativeMethods.CreateVersionStringFromModule(
            processId, modulePath, null, 0, out uint length);
        if (sizeResult != insufficientBuffer)
        {
            CorDebugHResult.ThrowIfFailed(sizeResult, "CreateVersionStringFromModule");
        }

        if (length is 0 or > maximumVersionLength)
        {
            throw new InvalidOperationException("The runtime debugger identifier exceeds its size limit.");
        }

        Span<char> buffer = stackalloc char[checked((int)length)];
        fixed (char* pointer = buffer)
        {
            CorDebugHResult.ThrowIfFailed(
                DbgShimNativeMethods.CreateVersionStringFromModule(
                    processId, modulePath, pointer, length, out uint written),
                "CreateVersionStringFromModule");
            if (written is 0 || written > length || buffer[checked((int)written - 1)] != '\0')
            {
                throw new InvalidOperationException("The runtime debugger identifier is not terminated correctly.");
            }

            nint debugger = 0;
            try
            {
                CorDebugHResult.ThrowIfFailed(
                    DbgShimNativeMethods.CreateDebuggingInterfaceFromVersionEx(
                        corDebugVersion4, pointer, out debugger),
                    "CreateDebuggingInterfaceFromVersionEx");
                if (debugger == 0)
                {
                    throw new InvalidOperationException("The runtime returned no debugger interface.");
                }

                nint result = debugger;
                debugger = 0;
                return result;
            }
            finally
            {
                if (debugger != 0)
                {
                    _ = ComAbi.Release(debugger);
                }
            }
        }
    }

    /// <summary>
    /// Gets the sole initialized CoreCLR module path in a target process.
    /// </summary>
    /// <param name="processId">The target operating-system process identifier.</param>
    /// <returns>The absolute runtime module path reported by dbgshim.</returns>
    internal static unsafe string GetSingleRuntimePath(uint processId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        nint handles = 0;
        nint paths = 0;
        uint count = 0;
        int result = DbgShimNativeMethods.EnumerateClrs(
            processId,
            out handles,
            out paths,
            out count);
        try
        {
            CorDebugHResult.ThrowIfFailed(result, "EnumerateCLRs");
            if (result != 0 || count == 0 || handles == 0 || paths == 0)
            {
                throw new InvalidOperationException(
                    $"Process {processId} has not loaded an attachable CoreCLR runtime.");
            }

            if (count != 1)
            {
                throw new InvalidOperationException(
                    $"Process {processId} contains {count} CoreCLR runtimes; select one explicitly.");
            }

            nint startupHandle = *(nint*)handles;
            if (startupHandle == -1)
            {
                throw new InvalidOperationException(
                    $"Process {processId} is still initializing its CoreCLR runtime.");
            }

            nint pathPointer = *(nint*)paths;
            string? path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException(
                    $"Process {processId} reported an invalid CoreCLR module path.");
            }

            return Path.GetFullPath(path);
        }
        finally
        {
            if (handles != 0 || paths != 0 || count != 0)
            {
                CorDebugHResult.ThrowIfFailed(
                    DbgShimNativeMethods.CloseClrEnumeration(handles, paths, count),
                    "CloseCLREnumeration");
            }
        }
    }
}
