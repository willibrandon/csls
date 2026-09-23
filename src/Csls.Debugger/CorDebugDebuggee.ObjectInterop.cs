using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves runtime classes used by managed object expansion.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static unsafe nint GetObjectClass(nint instance)
    {
        nint result = 0;
        nint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugObjectValueAbi(instance).GetClass((nint)resultAddress),
            "ICorDebugObjectValue.GetClass");
        return RequirePointer(result, "ICorDebugObjectValue.GetClass");
    }

    private static unsafe nint GetClassModule(nint runtimeClass)
    {
        nint result = 0;
        nint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetModule((nint)resultAddress),
            "ICorDebugClass.GetModule");
        return RequirePointer(result, "ICorDebugClass.GetModule");
    }

    private static unsafe uint GetClassToken(nint runtimeClass)
    {
        uint result = 0;
        uint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetToken((nint)resultAddress),
            "ICorDebugClass.GetToken");
        return Volatile.Read(ref *resultAddress);
    }

    private static unsafe nint GetModuleClass(nint module, uint typeToken)
    {
        nint result = 0;
        nint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugModuleAbi(module).GetClassFromToken(typeToken, (nint)resultAddress),
            "ICorDebugModule.GetClassFromToken");
        return RequirePointer(result, "ICorDebugModule.GetClassFromToken");
    }

    private static nint RequirePointer(nint value, string operation) =>
        Volatile.Read(ref value) != 0
            ? value
            : throw new InvalidOperationException($"{operation} returned no value.");
}
