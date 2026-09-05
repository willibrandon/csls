using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Applies method and type step exclusions after module-level JMC activation.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private const uint MethodDefinitionTokenKind = 0x06000000;
    private const uint TokenKindMask = 0xff000000;
    private const uint TypeDefinitionTokenKind = 0x02000000;

    private static void ApplyStepFilters(
        CorDebugLoadedModule module,
        uint[] excludedTokens)
    {
        int failureCount = excludedTokens.Count(token =>
            !TryExcludeToken(module.Pointer, token));

        if (failureCount > 0)
        {
            module.JustMyCodeDiagnostic =
                $"The runtime rejected {failureCount} of {excludedTokens.Length} step filters.";
        }
    }

    private static bool TryExcludeToken(nint module, uint token) =>
        (token & TokenKindMask) switch
        {
            MethodDefinitionTokenKind => TryExcludeMethod(module, token),
            TypeDefinitionTokenKind => TryExcludeType(module, token),
            _ => false
        };

    private static unsafe bool TryExcludeMethod(nint module, uint token)
    {
        nint function = 0;
        nint function2 = 0;
        try
        {
            nint* functionAddress = &function;
            if (new ICorDebugModuleAbi(module).GetFunctionFromToken(
                token,
                (nint)functionAddress) < 0)
            {
                return false;
            }

            function = Volatile.Read(ref *functionAddress);
            return function != 0 && ComAbi.TryQueryInterface(
                function,
                ICorDebugFunction2Abi.InterfaceId,
                out function2) &&
                new ICorDebugFunction2Abi(function2).SetJMCStatus(bIsJustMyCode: 0) >= 0;
        }
        finally
        {
            ReleaseInterfaces(function2, function);
        }
    }

    private static unsafe bool TryExcludeType(nint module, uint token)
    {
        nint type = 0;
        nint type2 = 0;
        try
        {
            nint* typeAddress = &type;
            if (new ICorDebugModuleAbi(module).GetClassFromToken(token, (nint)typeAddress) < 0)
            {
                return false;
            }

            type = Volatile.Read(ref *typeAddress);
            return type != 0 && ComAbi.TryQueryInterface(
                type,
                ICorDebugClass2Abi.InterfaceId,
                out type2) &&
                new ICorDebugClass2Abi(type2).SetJMCStatus(bIsJustMyCode: 0) >= 0;
        }
        finally
        {
            ReleaseInterfaces(type2, type);
        }
    }

    private static void ReleaseInterfaces(nint derived, nint primary)
    {
        if (derived != 0)
        {
            _ = ComAbi.Release(derived);
        }

        if (primary != 0)
        {
            _ = ComAbi.Release(primary);
        }
    }
}
