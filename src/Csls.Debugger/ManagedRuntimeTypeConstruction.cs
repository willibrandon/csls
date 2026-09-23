using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Constructs exact native runtime types from scoped definitions and borrowed generic arguments.
/// </summary>
internal static class ManagedRuntimeTypeConstruction
{
    /// <summary>
    /// Returns an owned type reference while preserving ownership of every supplied generic argument.
    /// </summary>
    /// <param name="module">The borrowed defining runtime module.</param>
    /// <param name="token">The module-scoped type-definition token.</param>
    /// <param name="isValueType">Whether the signature declares a value type.</param>
    /// <param name="arguments">The borrowed exact type arguments in declaration order.</param>
    /// <returns>The exact type reference owned by the caller.</returns>
    internal static unsafe nint Create(nint module, uint token, bool isValueType, ReadOnlySpan<nint> arguments)
    {
        nint runtimeClass = 0;
        nint class2 = 0;
        nint result = 0;
        try
        {
            int hr = new ICorDebugModuleAbi(module).GetClassFromToken(token, (nint)(&runtimeClass));
            runtimeClass = Volatile.Read(ref runtimeClass);
            CorDebugHResult.ThrowIfFailed(hr, "ICorDebugModule.GetClassFromToken");
            class2 = ComAbi.QueryInterface(RequirePointer(runtimeClass, "ICorDebugModule.GetClassFromToken"),
                ICorDebugClass2Abi.InterfaceId);
            fixed (nint* argumentAddress = arguments)
            {
                hr = new ICorDebugClass2Abi(class2).GetParameterizedType(isValueType ? 0x11u : 0x12u,
                    checked((uint)arguments.Length), (nint)argumentAddress, (nint)(&result));
            }
            result = Volatile.Read(ref result);
            CorDebugHResult.ThrowIfFailed(hr, "ICorDebugClass2.GetParameterizedType");
            return RequirePointer(result, "ICorDebugClass2.GetParameterizedType");
        }
        catch
        {
            Release(result);
            throw;
        }
        finally
        {
            Release(class2);
            Release(runtimeClass);
        }
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
