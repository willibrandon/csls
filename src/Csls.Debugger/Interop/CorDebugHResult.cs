using System.Runtime.InteropServices;

namespace Csls.Debugger.Interop;

/// <summary>
/// Converts native debugger HRESULT failures into contextual managed exceptions.
/// </summary>
internal static class CorDebugHResult
{
    /// <summary>
    /// Throws when a native debugger operation returned a failing HRESULT.
    /// </summary>
    /// <param name="hresult">The HRESULT returned by the native operation.</param>
    /// <param name="operation">The stable operation name used in diagnostics.</param>
    internal static void ThrowIfFailed(int hresult, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (hresult >= 0)
        {
            return;
        }

        Exception? cause = Marshal.GetExceptionForHR(hresult);
        throw new InvalidOperationException(
            $"{operation} failed with HRESULT 0x{hresult:X8}.",
            cause);
    }
}
