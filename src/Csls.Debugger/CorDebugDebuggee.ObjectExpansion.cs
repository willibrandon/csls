using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Expands managed object fields from runtime values and ECMA-335 metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe List<DebugVariableInfo> ExpandObject(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        nint instance = 0;
        nint value2 = 0;
        nint currentType = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");

            var result = new List<DebugVariableInfo>();
            int fieldIndex = 0;
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
                    ReadDeclaredInstanceFields(
                        result,
                        instance,
                        runtimeClass,
                        peReader.GetMetadataReader(),
                        typeToken,
                        parentEvaluateName,
                        frameId,
                        generation,
                        start,
                        count,
                        ref fieldIndex);
                    if (count > 0 && result.Count == count)
                    {
                        return result;
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

            return result;
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
        }
    }

    private void ReadDeclaredInstanceFields(
        List<DebugVariableInfo> result,
        nint instance,
        nint declaringClass,
        MetadataReader metadata,
        uint typeToken,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ref int fieldIndex)
    {
        TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
            checked((int)(typeToken & 0x00FFFFFF)));
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
        {
            FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) != 0)
            {
                continue;
            }

            if (fieldIndex >= start && (count == 0 || result.Count < count))
            {
                result.Add(ReadInstanceField(
                    instance,
                    declaringClass,
                    metadata,
                    fieldHandle,
                    field,
                    parentEvaluateName,
                    frameId,
                    generation));
            }

            fieldIndex++;
            if (count > 0 && result.Count == count)
            {
                return;
            }

            if (fieldIndex > MaximumExpandableValueCount)
            {
                throw new InvalidOperationException(
                    $"The object exceeds the field limit of {MaximumExpandableValueCount}.");
            }
        }
    }

    private unsafe DebugVariableInfo ReadInstanceField(
        nint instance,
        nint declaringClass,
        MetadataReader metadata,
        FieldDefinitionHandle fieldHandle,
        FieldDefinition field,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation)
    {
        nint fieldValue = 0;
        nint* fieldValueAddress = &fieldValue;
        uint fieldToken = checked((uint)MetadataTokens.GetToken(fieldHandle));
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugObjectValueAbi(instance).GetFieldValue(
                declaringClass,
                fieldToken,
                (nint)fieldValueAddress),
            "ICorDebugObjectValue.GetFieldValue");
        fieldValue = Volatile.Read(ref *fieldValueAddress);
        if (fieldValue == 0)
        {
            throw new InvalidOperationException(
                "ICorDebugObjectValue.GetFieldValue returned no value.");
        }

        try
        {
            ManagedValueDisplay display = FormatRuntimeValue(fieldValue);
            string name = metadata.GetString(field.Name);
            string? evaluateName = CreateMemberEvaluateName(parentEvaluateName, name);
            ManagedValueReferences references = RetainValue(
                fieldValue,
                generation,
                evaluateName,
                frameId);
            return new DebugVariableInfo(
                name,
                display.Value,
                display.Type,
                references.VariablesReference,
                references.MemoryReference,
                evaluateName);
        }
        finally
        {
            _ = ComAbi.Release(fieldValue);
        }
    }

    private static string? CreateMemberEvaluateName(string? parent, string name)
    {
        if (parent is null || !IsSimpleIdentifier(name))
        {
            return null;
        }

        return $"{parent}.{name}";
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '_' && !char.IsLetterOrDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

}
