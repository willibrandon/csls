using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed function-evaluation targets from CLR metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumFunctionEvaluationHierarchyDepth = 256;

    private unsafe ManagedFunctionBinding ResolveInstanceFunction(
        nint receiver,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments,
        nint thread)
    {
        nint value2 = 0;
        nint currentType = 0;
        try
        {
            value2 = ComAbi.QueryInterface(receiver, ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");

            for (int depth = 0;
                currentType != 0 && depth < MaximumFunctionEvaluationHierarchyDepth;
                depth++)
            {
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    runtimeClass = GetRuntimeTypeClass(currentType);
                    module = GetClassModule(runtimeClass);
                    uint typeToken = GetClassToken(runtimeClass);
                    CorDebugLoadedModule loadedModule = _sourceBreakpoints.FindModule(module)
                        ?? throw new InvalidOperationException("The method's runtime module is unavailable.");
                    uint? methodToken = ManagedFunctionMethodResolver.Resolve(
                        loadedModule,
                        typeToken,
                        methodName,
                        language,
                        arguments,
                        staticMethod: false);
                    if (methodToken is uint resolvedMethodToken)
                    {
                        ManagedBoundType declaringType = _boundTypes.CaptureType(currentType, thread);
                        ManagedBoundType? resultType = _boundTypes.BindMethodResult(
                            module, resolvedMethodToken, declaringType.TypeArguments, thread);
                        nint[] typeArguments = ManagedRuntimeTypeArguments.Retain(currentType);
                        try
                        {
                            return new ManagedFunctionBinding(
                                GetModuleFunction(module, resolvedMethodToken), typeArguments, resultType);
                        }
                        catch
                        {
                            foreach (nint argument in typeArguments)
                            {
                                _ = ComAbi.Release(argument);
                            }

                            throw;
                        }
                    }

                    nint* baseTypeAddress = &baseType;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                        "ICorDebugType.GetBase");
                    baseType = Volatile.Read(ref *baseTypeAddress);
                }
                finally
                {
                    if (module != 0)
                    {
                        _ = ComAbi.Release(module);
                    }

                    if (runtimeClass != 0)
                    {
                        _ = ComAbi.Release(runtimeClass);
                    }

                    if (currentType != 0)
                    {
                        _ = ComAbi.Release(currentType);
                    }

                    currentType = baseType;
                }
            }

            if (currentType != 0)
            {
                throw new InvalidOperationException(
                    $"The runtime type hierarchy exceeds the supported depth of " +
                    $"{MaximumFunctionEvaluationHierarchyDepth}.");
            }
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }

            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
            }
        }

        throw new InvalidOperationException(
            $"No instance method named '{methodName}' with {arguments.Length} argument(s) " +
            "is available on the runtime type hierarchy.");
    }


    private static unsafe nint GetRuntimeTypeClass(nint type)
    {
        nint runtimeClass = 0;
        nint* runtimeClassAddress = &runtimeClass;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugTypeAbi(type).GetClass((nint)runtimeClassAddress),
            "ICorDebugType.GetClass");
        return RequirePointer(
            Volatile.Read(ref *runtimeClassAddress),
            "ICorDebugType.GetClass");
    }
}
