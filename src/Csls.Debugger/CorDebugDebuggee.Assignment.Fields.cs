using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves writable instance fields through exact runtime type hierarchies.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedAssignmentTarget ResolveFieldAssignmentTarget(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        ManagedExpressionValue receiver = EvaluateNode(
            frame,
            plan,
            node.Children[0],
            generation);
        nint parent = DereferenceValue(GetRuntimeValue(receiver));
        try
        {
            ManagedValueTypeAssignment.ValidateFieldParent(parent);
        }
        finally
        {
            _ = ComAbi.Release(parent);
        }

        string? evaluateName = ManagedExpressionName.CreateMember(receiver.Display.EvaluateName, node.Text!);
        return ManagedAssignmentTarget.TakeOwnership(
            ResolveInstanceFieldValue(receiver, node.Text!, plan.Language), evaluateName);
    }

    private unsafe (
        nint Value,
        ManagedTupleCustomTypeInfo? TupleCustomTypeInfo,
        ManagedValueOrigin? Origin) ResolveInstanceFieldValue(
        ManagedExpressionValue receiver,
        string name,
        DebugExpressionLanguage language)
    {
        nint runtimeValue = GetRuntimeValue(receiver);
        nint dereferenced = 0;
        nint instance = 0;
        nint value2 = 0;
        nint currentType = 0;
        try
        {
            dereferenced = DereferenceValue(runtimeValue);
            instance = ComAbi.QueryInterface(
                dereferenced,
                ICorDebugObjectValueAbi.InterfaceId);
            value2 = ComAbi.QueryInterface(
                dereferenced,
                ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");

            StringComparison comparison = language ==
                DebugExpressionLanguage.VisualBasic
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            ManagedTupleCustomTypeInfo? tupleCustomTypeInfo = GetExpressionTupleCustomTypeInfo(receiver);
            ManagedValueOrigin? origin = GetValueOrigin(receiver);
            if (_tuplePresenter.TryGetElementValue(
                dereferenced,
                currentType,
                tupleCustomTypeInfo,
                name,
                comparison,
                out (nint Value, ManagedTupleCustomTypeInfo? CustomTypeInfo, ManagedValueOrigin? Origin) tupleElement,
                origin))
            {
                return tupleElement;
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
                    using PEReader peReader = _sourceBreakpoints
                        .FindModule(module)
                        ?.OpenPeReader() ?? new PEReader(new FileStream(
                            CorDebugModulePath.Get(module),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read | FileShare.Delete));
                    MetadataReader metadata = peReader.GetMetadataReader();
                    uint? fieldToken = TryResolveDeclaredInstanceField(
                        metadata,
                        typeToken,
                        name,
                        language);
                    if (fieldToken is uint resolvedFieldToken)
                    {
                        FieldDefinition field = metadata.GetFieldDefinition(
                            MetadataTokens.FieldDefinitionHandle(
                                checked((int)(resolvedFieldToken & 0x00FFFFFF))));
                        ManagedTupleCustomTypeInfo? fieldTupleInfo = _tupleTypeShape.GetFieldCustomTypeInfo(
                            currentType, metadata, field, depth == 0 ? tupleCustomTypeInfo : null);
                        ManagedValueOrigin? fieldOrigin = CreateFieldValueOrigin(
                            origin, runtimeClass, resolvedFieldToken);
                        return (
                            GetObjectFieldValue(instance, runtimeClass, resolvedFieldToken),
                            fieldTupleInfo,
                            fieldOrigin);
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

            if (instance != 0)
            {
                _ = ComAbi.Release(instance);
            }

            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }

        throw new InvalidOperationException(
            $"Instance field '{name}' is unavailable on the runtime type hierarchy.");
    }

    private static uint? TryResolveDeclaredInstanceField(
        MetadataReader metadata,
        uint typeToken,
        string fieldName,
        DebugExpressionLanguage language)
    {
        EntityHandle entity = MetadataTokens.EntityHandle(checked((int)typeToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{typeToken:X8} is not a TypeDef token.");
        }

        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)entity);
        foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
        {
            FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) == 0 &&
                string.Equals(metadata.GetString(field.Name), fieldName, comparison))
            {
                return checked((uint)MetadataTokens.GetToken(fieldHandle));
            }
        }

        return null;
    }

    private static unsafe nint GetObjectFieldValue(
        nint instance,
        nint declaringClass,
        uint fieldToken)
    {
        nint fieldValue = 0;
        nint* fieldValueAddress = &fieldValue;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugObjectValueAbi(instance).GetFieldValue(
                declaringClass,
                fieldToken,
                (nint)fieldValueAddress),
            "ICorDebugObjectValue.GetFieldValue");
        return RequirePointer(
            Volatile.Read(ref *fieldValueAddress),
            "ICorDebugObjectValue.GetFieldValue");
    }
}
