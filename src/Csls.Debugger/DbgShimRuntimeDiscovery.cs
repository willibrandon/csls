using Csls.Debugger.Interop;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Discovers and validates the CoreCLR instance selected for local attachment.
/// </summary>
internal static class DbgShimRuntimeDiscovery
{
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
