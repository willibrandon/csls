using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Reads source parameters from exact state-machine storage shared by inspection and assignment.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private List<DebugVariableInfo> EnumerateCapturedArguments(
        ManagedFrameHandle frame, IReadOnlyList<ManagedCapturedArgument> arguments,
        DebugStopGeneration generation, int start, int count)
    {
        var result = new List<DebugVariableInfo>();
        foreach (ManagedCapturedArgument argument in arguments.Skip(start).Take(count == 0 ? arguments.Count : count))
        {
            (nint Value, ManagedTupleCustomTypeInfo? TupleCustomTypeInfo,
                ManagedValueOrigin? Origin, ManagedBoundType? DeclaredType) resolved = ResolveCapturedArgument(frame, argument);
            if (resolved.Value == 0)
            {
                result.Add(new DebugVariableInfo(argument.Name, "<optimized out>",
                    FormatCapturedParameterType(frame, resolved.DeclaredType, argument.TupleCustomTypeInfo), 0, null, null,
                    DebugVariablePresentationKind.Unavailable));
                continue;
            }

            using var value = ManagedAssignmentTarget.TakeOwnership(resolved, argument.Name);
            ManagedValueDisplay display = FormatRuntimeValue(value.Pointer, value.TupleCustomTypeInfo);
            ManagedValueReferences references = RetainValue(value.Pointer, generation, argument.Name, frame.Id,
                tupleCustomTypeInfo: value.TupleCustomTypeInfo, origin: value.Origin);
            result.Add(new DebugVariableInfo(argument.Name, display.Value, display.Type,
                references.VariablesReference, references.MemoryReference, argument.Name));
        }

        return result;
    }

    private string FormatCapturedParameterType(
        ManagedFrameHandle frame, ManagedBoundType? declaredType, ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
    {
        if (declaredType is null)
        {
            return string.Empty;
        }

        if (FormatPrimitiveRuntimeType(declaredType.ElementType) is string primitive)
        {
            return primitive;
        }

        nint thread = 0;
        nint type = 0;
        try
        {
            thread = GetThread(frame.ThreadId);
            type = _boundTypes.ResolveRuntimeType(declaredType, thread);
            return FormatRuntimeType(type, depth: 0, tupleCustomTypeInfo, out _);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(type);
            ReleaseFunctionEvaluationPointer(thread);
        }
    }

    private unsafe (nint Value, ManagedTupleCustomTypeInfo? TupleCustomTypeInfo,
        ManagedValueOrigin? Origin, ManagedBoundType? DeclaredType) ResolveCapturedArgument(
        ManagedFrameHandle frame, ManagedCapturedArgument argument)
    {
        nint receiver = GetFrameAssignmentTarget(frame.Pointer, ManagedScopeKind.Arguments, 0);
        nint dereferenced = 0;
        nint instance = 0;
        nint value2 = 0;
        nint exactType = 0;
        nint runtimeClass = 0;
        nint module = 0;
        nint thread = 0;
        try
        {
            dereferenced = DereferenceValue(receiver);
            instance = ComAbi.QueryInterface(dereferenced, ICorDebugObjectValueAbi.InterfaceId);
            value2 = ComAbi.QueryInterface(dereferenced, ICorDebugValue2Abi.InterfaceId);
            nint* typeAddress = &exactType;
            CorDebugHResult.ThrowIfFailed(new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress),
                "ICorDebugValue2.GetExactType");
            exactType = RequirePointer(Volatile.Read(ref *typeAddress), "ICorDebugValue2.GetExactType");
            runtimeClass = GetRuntimeTypeClass(exactType);
            module = GetClassModule(runtimeClass);
            using PEReader pe = OpenRuntimeModule(module);
            thread = GetThread(frame.ThreadId);
            uint? token = TryResolveDeclaredInstanceField(pe.GetMetadataReader(), GetClassToken(runtimeClass),
                argument.FieldName, frame.ExpressionLanguage);
            if (token is null)
            {
                ManagedBoundType? sourceType = argument.ParameterIndex is int index
                    ? ResolveCapturedParameterType(frame, argument.MethodToken, index, exactType, module, thread)
                    : null;
                return (0, argument.TupleCustomTypeInfo, null, sourceType);
            }

            ManagedBoundType declaredType = _boundTypes.BindField(exactType, module, token.Value, thread);
            ManagedValueOrigin? origin = CreateFieldValueOrigin(
                frame.CreateValueOrigin(ManagedScopeKind.Arguments, 0), runtimeClass, token.Value);
            return (GetObjectFieldValue(instance, runtimeClass, token.Value), argument.TupleCustomTypeInfo, origin, declaredType);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(thread);
            ReleaseFunctionEvaluationPointer(module);
            ReleaseFunctionEvaluationPointer(runtimeClass);
            ReleaseFunctionEvaluationPointer(exactType);
            ReleaseFunctionEvaluationPointer(value2);
            ReleaseFunctionEvaluationPointer(instance);
            ReleaseFunctionEvaluationPointer(dereferenced);
            ReleaseFunctionEvaluationPointer(receiver);
        }
    }

    private ManagedBoundType ResolveCapturedParameterType(
        ManagedFrameHandle frame, uint methodToken, int parameterIndex, nint stateType, nint module, nint thread)
    {
        using PEReader pe = OpenRuntimeModule(module);
        using var metadata = new ManagedMetadataImage(pe.GetMetadataReader(), frame.MetadataDeltas);
        var method = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)methodToken));
        MethodSignature<ManagedMetadataTypeSignature> signature = metadata.DecodeMethodSignature(method, module);
        (MetadataReader reader, EntityHandle relative) = metadata.Resolve(metadata.GetDeclaringType(method));
        int typeParameterCount = reader.GetTypeDefinition((TypeDefinitionHandle)relative).GetGenericParameters().Count;
        IReadOnlyList<ManagedBoundType> arguments = _boundTypes.CaptureType(stateType, thread).TypeArguments;
        return _boundTypes.Bind(signature.ParameterTypes[parameterIndex],
            [.. arguments.Take(typeParameterCount)], [.. arguments.Skip(typeParameterCount)], thread);
    }
}
