using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded IL from the runtime's current function version while the engine actor owns the stop.
/// </summary>
internal static class CorDebugMethodBodyReader
{
    /// <summary>
    /// Copies a current method body while keeping all native function and code references operation-owned.
    /// </summary>
    /// <param name="module">The borrowed stopped runtime module.</param>
    /// <param name="methodToken">The exact method-definition token.</param>
    /// <param name="maximumBytes">The largest method body accepted by the caller.</param>
    /// <param name="localSignatureToken">Receives the local declaration token from the same current method version.</param>
    /// <returns>The complete IL bytes, or null when the body exceeds the caller's bound.</returns>
    internal static unsafe byte[]? Read(nint module, uint methodToken, int maximumBytes, out uint localSignatureToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        localSignatureToken = 0;
        nint function = 0;
        nint code = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(new ICorDebugModuleAbi(module).GetFunctionFromToken(methodToken,
                (nint)functionAddress), "ICorDebugModule.GetFunctionFromToken");
            function = RequirePointer(Volatile.Read(ref *functionAddress));
            uint localToken = 0;
            uint* localTokenAddress = &localToken;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFunctionAbi(function).GetLocalVarSigToken((nint)localTokenAddress),
                "ICorDebugFunction.GetLocalVarSigToken");
            localSignatureToken = Volatile.Read(ref *localTokenAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = RequirePointer(Volatile.Read(ref *codeAddress));
            uint size = 0;
            uint* sizeAddress = &size;
            var api = new ICorDebugCodeAbi(code);
            CorDebugHResult.ThrowIfFailed(api.GetSize((nint)sizeAddress), "ICorDebugCode.GetSize");
            size = Volatile.Read(ref *sizeAddress);
            if (size == 0 || size > maximumBytes)
            {
                return null;
            }

            byte[] bytes = new byte[size];
            uint read = 0;
            uint* readAddress = &read;
            fixed (byte* buffer = bytes)
            {
                CorDebugHResult.ThrowIfFailed(api.GetCode(0, size, size, (nint)buffer, (nint)readAddress),
                    "ICorDebugCode.GetCode");
            }
            if (Volatile.Read(ref *readAddress) != size)
            {
                throw new InvalidDataException("The runtime returned an incomplete method body.");
            }
            return bytes;
        }
        finally
        {
            if (code != 0)
            {
                _ = ComAbi.Release(code);
            }
            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private static nint RequirePointer(nint pointer) => pointer != 0 ? pointer
        : throw new InvalidOperationException("The runtime returned no method or IL code object.");
}
