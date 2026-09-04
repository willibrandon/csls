using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Normalizes nullable enumerables and boxes value-type receivers through CoreCLR.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe nint GetResultsViewTarget(nint value)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint type = 0;
        try
        {
            nint* typeAddress = &type;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress),
                "ICorDebugValue2.GetExactType");
            type = RequirePointer(Volatile.Read(ref *typeAddress), "ICorDebugValue2.GetExactType");
            return IsNullableType(type) ? RetainNullableResultsViewTarget(value, type) : Retain(value);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(type);
            ReleaseFunctionEvaluationPointer(value2);
        }
    }

    private nint RetainNullableResultsViewTarget(nint value, nint type)
    {
        nint instance = 0;
        nint runtimeClass = 0;
        nint module = 0;
        nint hasValue = 0;
        nint containedValue = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader reader = OpenRuntimeModule(module);
            MetadataReader metadata = reader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            foreach (FieldDefinitionHandle field in metadata.GetTypeDefinition(handle).GetFields())
            {
                string name = metadata.GetString(metadata.GetFieldDefinition(field).Name);
                if (name == "hasValue")
                {
                    hasValue = GetObjectFieldValue(instance, runtimeClass,
                        checked((uint)MetadataTokens.GetToken(field)));
                }
                else if (name == "value")
                {
                    containedValue = GetObjectFieldValue(instance, runtimeClass,
                        checked((uint)MetadataTokens.GetToken(field)));
                }
            }

            if (hasValue == 0 || containedValue == 0)
            {
                throw new InvalidOperationException("The nullable value has no inspectable runtime fields.");
            }

            if (CorDebugValueFormatter.Format(hasValue).Value != "true")
            {
                return 0;
            }

            nint result = containedValue;
            containedValue = 0;
            return result;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(containedValue);
            ReleaseFunctionEvaluationPointer(hasValue);
            ReleaseFunctionEvaluationPointer(module);
            ReleaseFunctionEvaluationPointer(runtimeClass);
            ReleaseFunctionEvaluationPointer(instance);
        }
    }

    private static unsafe nint RetainResultsViewStructReceiver(nint value)
    {
        uint elementType = 0;
        uint* elementTypeAddress = &elementType;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetType((nint)elementTypeAddress),
            "ICorDebugValue.GetType");
        return Volatile.Read(ref *elementTypeAddress) == 0x11 ? Retain(value) : 0;
    }

    private unsafe nint ResolveResultsViewBoxingFunction(nint value)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint currentType = 0;
        try
        {
            nint* typeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(Volatile.Read(ref *typeAddress), "ICorDebugValue2.GetExactType");
            for (int depth = 0; currentType != 0 && depth < MaximumFunctionEvaluationHierarchyDepth; depth++)
            {
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    runtimeClass = GetRuntimeTypeClass(currentType);
                    module = GetClassModule(runtimeClass);
                    uint token = GetClassToken(runtimeClass);
                    if (TryResolveResultsViewBoxingMethod(module, token) is uint method)
                    {
                        return GetModuleFunction(module, method);
                    }

                    nint* baseTypeAddress = &baseType;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                        "ICorDebugType.GetBase");
                    baseType = Volatile.Read(ref *baseTypeAddress);
                }
                finally
                {
                    ReleaseFunctionEvaluationPointer(module);
                    ReleaseFunctionEvaluationPointer(runtimeClass);
                    ReleaseFunctionEvaluationPointer(currentType);
                    currentType = baseType;
                }
            }

            throw new InvalidOperationException("The runtime object base type could not be resolved.");
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(currentType);
            ReleaseFunctionEvaluationPointer(value2);
        }
    }

    private uint? TryResolveResultsViewBoxingMethod(nint module, uint token)
    {
        using PEReader reader = OpenRuntimeModule(module);
        MetadataReader metadata = reader.GetMetadataReader();
        TypeDefinition definition = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(token & 0x00FFFFFF))));
        return definition.BaseType.IsNil && metadata.StringComparer.Equals(definition.Namespace, "System") &&
            metadata.StringComparer.Equals(definition.Name, "Object")
                ? TryResolveDeclaredMethod(metadata, token, "MemberwiseClone",
                    DebugExpressionLanguage.CSharp, [], staticMethod: false) ??
                    throw new InvalidOperationException("The runtime has no object-copy method.")
                : null;
    }

    private void ContinueResultsViewConstruction(
        ManagedFunctionEvaluation active,
        ManagedResultsViewEvaluation context,
        nint value)
    {
        nint target = 0;
        nint evaluation = 0;
        nint constructor = 0;
        nint[] typeArguments = [];
        try
        {
            // CoreCLR boxes a value-type receiver before Object.MemberwiseClone executes.
            // Its argument preparation protects embedded references before any managed code runs.
            target = CreateFunctionEvaluationHandle(value);
            evaluation = CreateEvaluation(active.Thread);
            constructor = context.DetachConstructor();
            typeArguments = context.DetachConstructorTypeArguments();
            ReleaseFunctionEvaluationPointer(active.Pointer);
            ReleaseFunctionEvaluationPointer(active.Function);
            active.Pointer = evaluation;
            active.Function = constructor;
            active.TypeArguments = typeArguments;
            active.Receiver = 0;
            active.RuntimeArguments[0] = target;
            active.Arguments = [context.EnumerableArgument];
            active.ConstructsObject = true;
            active.MethodCallScheduled = false;
            context.EnumerableBoxingCompleted = true;
            evaluation = 0;
            constructor = 0;
            typeArguments = [];
            target = 0;
            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after scheduling Results View construction. " +
                "The evaluation state is uncertain; this debugger session must be disconnected.");
        }
        finally
        {
            ReleaseFunctionEvaluationHandle(target);
            ReleaseFunctionEvaluationPointer(evaluation);
            ReleaseFunctionEvaluationPointer(constructor);
            foreach (nint argument in typeArguments)
            {
                ReleaseFunctionEvaluationPointer(argument);
            }
        }
    }
}
