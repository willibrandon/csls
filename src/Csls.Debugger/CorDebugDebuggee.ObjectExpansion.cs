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
        DebugStopGeneration generation,
        int start,
        int count)
    {
        nint instance = 0;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            runtimeClass = GetObjectClass(instance);
            module = GetClassModule(runtimeClass);
            uint typeToken = GetClassToken(runtimeClass);
            string modulePath = CorDebugModulePath.Get(module);
            using FileStream stream = File.OpenRead(modulePath);
            using var peReader = new PEReader(stream);
            MetadataReader metadata = peReader.GetMetadataReader();
            return ReadInstanceFields(
                instance,
                module,
                runtimeClass,
                metadata,
                typeToken,
                parentEvaluateName,
                generation,
                start,
                count);
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

            if (instance != 0)
            {
                _ = ComAbi.Release(instance);
            }
        }
    }

    private unsafe List<DebugVariableInfo> ReadInstanceFields(
        nint instance,
        nint module,
        nint runtimeClass,
        MetadataReader metadata,
        uint initialTypeToken,
        string? parentEvaluateName,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        var result = new List<DebugVariableInfo>();
        uint typeToken = initialTypeToken;
        int fieldIndex = 0;
        while (typeToken != 0)
        {
            TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(typeToken & 0x00FFFFFF)));
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            nint declaringClass = typeToken == initialTypeToken
                ? Retain(runtimeClass)
                : GetModuleClass(module, typeToken);
            try
            {
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
                            generation));
                    }

                    fieldIndex++;
                    if (count > 0 && result.Count == count)
                    {
                        return result;
                    }

                    if (fieldIndex > MaximumExpandableValueCount)
                    {
                        throw new InvalidOperationException(
                            $"The object exceeds the field limit of {MaximumExpandableValueCount}.");
                    }
                }
            }
            finally
            {
                _ = ComAbi.Release(declaringClass);
            }

            EntityHandle baseType = type.BaseType;
            typeToken = baseType.Kind == HandleKind.TypeDefinition
                ? checked((uint)MetadataTokens.GetToken((TypeDefinitionHandle)baseType))
                : 0;
        }

        return result;
    }

    private unsafe DebugVariableInfo ReadInstanceField(
        nint instance,
        nint declaringClass,
        MetadataReader metadata,
        FieldDefinitionHandle fieldHandle,
        FieldDefinition field,
        string? parentEvaluateName,
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
            ManagedValueDisplay display = CorDebugValueFormatter.Format(fieldValue);
            string name = metadata.GetString(field.Name);
            string? evaluateName = CreateMemberEvaluateName(parentEvaluateName, name);
            ManagedValueReferences references = RetainValue(
                fieldValue,
                generation,
                evaluateName);
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
