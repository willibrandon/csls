using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed function-evaluation targets from CLR metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumFunctionEvaluationHierarchyDepth = 256;

    private ManagedBoundType?[] BindFunctionEvaluationArgumentTypes(
        ManagedExpressionValue[] arguments,
        DebugExpressionLanguage language,
        nint thread)
    {
        var types = new ManagedBoundType?[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            ManagedExpressionValue argument = arguments[index];
            if (argument.IsContextualDefault)
            {
                types[index] = null;
                continue;
            }

            if (argument.DeclaredType is ManagedBoundType declared)
            {
                types[index] = declared;
            }
            else if (argument is { HasScalar: true, Scalar: null, RuntimeValueReference: 0 })
            {
                types[index] = null;
            }
            else if (argument.RuntimeValueReference > 0)
            {
                types[index] = _boundTypes.CaptureValue(GetRuntimeValue(argument), thread);
            }
            else
            {
                DebugExpressionLanguage typeLanguage = argument.HasScalar &&
                    ManagedRuntimeTypeAliases.TryNormalize(
                        argument.Type, DebugExpressionLanguage.CSharp, out _, out _)
                    ? DebugExpressionLanguage.CSharp
                    : language;
                types[index] = _boundTypes.BindName(argument.Type, typeLanguage, thread);
            }
        }

        return types;
    }

    private unsafe ManagedFunctionBinding ResolveInstanceFunction(
        nint receiver,
        string methodName,
        DebugExpressionLanguage language,
        ManagedBoundType?[] arguments,
        IReadOnlyList<ManagedExpressionValue?> constantArguments,
        IReadOnlyList<string?> argumentNames,
        nint thread,
        ManagedBoundType? selectedReceiverType,
        uint? exactMethodToken = null)
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

            if (selectedReceiverType is not null &&
                (_boundTypes.GetAttributes(selectedReceiverType) & TypeAttributes.Interface) != 0)
            {
                ManagedBoundType actualReceiverType = _boundTypes.CaptureType(currentType, thread);
                if (!new ManagedReferenceConversion(_boundTypes).IsRuntimeAssignable(
                        actualReceiverType, selectedReceiverType, thread))
                {
                    throw new InvalidOperationException(
                        $"Runtime type '{actualReceiverType.DisplayName}' does not implement " +
                        $"interface '{selectedReceiverType.DisplayName}'.");
                }

                return ResolveInterfaceFunction(
                    selectedReceiverType, methodName, language, arguments, constantArguments,
                    argumentNames, thread, exactMethodToken);
            }

            bool selectedTypeReached = selectedReceiverType is null;
            if (ManagedRuntimeValueIdentity.GetElementType(receiver) is 0x14 or 0x1d)
            {
                ManagedBoundType arrayType = _boundTypes.CaptureType(currentType, thread);
                selectedTypeReached |= selectedReceiverType?.IsSameType(arrayType) == true;
                // CLR array types have no metadata class or GetBase implementation.
                // Their instance methods are declared by the loaded core library's System.Array.
                nint arrayBase = _boundTypes.ResolveRuntimeType(_boundTypes.GetParents(arrayType, thread)[0], thread);
                _ = ComAbi.Release(currentType);
                currentType = arrayBase;
            }
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
                    ManagedBoundType declaringType = _boundTypes.CaptureType(currentType, thread);
                    selectedTypeReached |= selectedReceiverType?.IsSameType(declaringType) == true;
                    (uint Token, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
                        ManagedExpressionValue?[] OptionalArguments,
                        ManagedBoundType[] MethodTypeArguments)? method = selectedTypeReached
                        ? exactMethodToken is uint getterToken
                            ? (getterToken, [], [], [], [])
                            : ManagedFunctionMethodResolver.ResolveCall(
                                loadedModule,
                                typeToken,
                                methodName,
                                language,
                                arguments,
                                staticMethod: false,
                                _boundTypes,
                                thread,
                                declaringType.TypeArguments,
                                constantArguments,
                                argumentNames)
                        : null;
                    if (method is { } resolvedMethod)
                    {
                        ManagedBoundType? resultType = _boundTypes.BindMethodResult(
                            module, resolvedMethod.Token, declaringType.TypeArguments, thread,
                            methodArguments: resolvedMethod.MethodTypeArguments);
                        nint[] declaringArguments = ManagedRuntimeTypeArguments.Retain(currentType);
                        nint[] methodArguments = [];
                        try
                        {
                            methodArguments = ManagedRuntimeTypeArguments.ResolveBound(
                                resolvedMethod.MethodTypeArguments, _boundTypes, thread);
                            nint[] typeArguments = [.. declaringArguments, .. methodArguments];
                            return new ManagedFunctionBinding(
                                GetModuleFunction(module, resolvedMethod.Token), typeArguments, resultType,
                                resolvedMethod.Parameters, resolvedMethod.ParameterSourceIndices,
                                resolvedMethod.OptionalArguments);
                        }
                        catch
                        {
                            foreach (nint argument in declaringArguments.Concat(methodArguments))
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

    private ManagedFunctionBinding ResolveInterfaceFunction(
        ManagedBoundType selectedInterface,
        string methodName,
        DebugExpressionLanguage language,
        ManagedBoundType?[] arguments,
        IReadOnlyList<ManagedExpressionValue?> constantArguments,
        IReadOnlyList<string?> argumentNames,
        nint thread,
        uint? exactMethodToken)
    {
        const int maximumInterfaces = 4096;
        var pending = new Queue<(ManagedBoundType Type, int Depth)>();
        var visited = new List<ManagedBoundType>();
        var matches = new List<(ManagedBoundType DeclaringType, CorDebugLoadedModule Module,
            uint Token, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
            ManagedExpressionValue?[] OptionalArguments, ManagedBoundType[] MethodTypeArguments)>();
        pending.Enqueue((selectedInterface, 0));
        int? matchingDepth = null;
        while (pending.TryDequeue(out (ManagedBoundType Type, int Depth) current))
        {
            if (matchingDepth is int depth && current.Depth > depth)
            {
                break;
            }

            if (visited.Count >= maximumInterfaces)
            {
                throw new InvalidOperationException(
                    "The runtime interface hierarchy exceeds the supported type budget.");
            }

            if (visited.Any(current.Type.IsSameType))
            {
                continue;
            }

            visited.Add(current.Type);
            CorDebugLoadedModule module = _boundTypes.GetModule(current.Type);
            (uint Token, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
                ManagedExpressionValue?[] OptionalArguments,
                ManagedBoundType[] MethodTypeArguments)? method =
                exactMethodToken is uint getterToken && current.Depth == 0
                    ? (getterToken, [], [], [], [])
                    : ManagedFunctionMethodResolver.ResolveCall(
                        module,
                        current.Type.DefinitionToken,
                        methodName,
                        language,
                        arguments,
                        staticMethod: false,
                        _boundTypes,
                        thread,
                        current.Type.TypeArguments,
                        constantArguments,
                        argumentNames,
                        allowAbstract: true);
            if (method is { } resolved)
            {
                matchingDepth = current.Depth;
                matches.Add((
                    current.Type,
                    module,
                    resolved.Token,
                    resolved.Parameters,
                    resolved.ParameterSourceIndices,
                    resolved.OptionalArguments,
                    resolved.MethodTypeArguments));
                continue;
            }

            foreach (ManagedBoundType parent in _boundTypes.GetParents(current.Type, thread).Where(
                parent => (_boundTypes.GetAttributes(parent) & TypeAttributes.Interface) != 0))
            {
                pending.Enqueue((parent, current.Depth + 1));
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No interface method named '{methodName}' with {arguments.Length} argument(s) " +
                $"is available on '{selectedInterface.DisplayName}'.");
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Interface method call '{methodName}' with {arguments.Length} argument(s) is " +
                $"ambiguous on '{selectedInterface.DisplayName}'.");
        }

        (ManagedBoundType declaringType, CorDebugLoadedModule declaringModule, uint token,
            ManagedBoundType[] parameters, int[] parameterSourceIndices,
            ManagedExpressionValue?[] optionalArguments,
            ManagedBoundType[] methodTypeArguments) = matches[0];
        ManagedBoundType? resultType = _boundTypes.BindMethodResult(
            declaringModule.Pointer,
            token,
            declaringType.TypeArguments,
            thread,
            methodArguments: methodTypeArguments);
        nint[] declaringArguments = ManagedRuntimeTypeArguments.ResolveBound(
            declaringType.TypeArguments, _boundTypes, thread);
        nint[] methodArguments = [];
        try
        {
            methodArguments = ManagedRuntimeTypeArguments.ResolveBound(
                methodTypeArguments, _boundTypes, thread);
            nint[] typeArguments = [.. declaringArguments, .. methodArguments];
            return new ManagedFunctionBinding(
                GetModuleFunction(declaringModule.Pointer, token),
                typeArguments,
                resultType,
                parameters,
                parameterSourceIndices,
                optionalArguments);
        }
        catch
        {
            foreach (nint argument in declaringArguments.Concat(methodArguments))
            {
                _ = ComAbi.Release(argument);
            }

            throw;
        }
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
