using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats managed values using exact CoreCLR types and loaded module metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumRuntimeTypeDepth = 64;
    private const int MaximumRuntimeTypeArgumentCount = 256;

    private ManagedValueDisplay FormatRuntimeValue(nint value)
    {
        nint inspectedValue = 0;
        nint value2 = 0;
        nint exactType = 0;
        try
        {
            bool hasInspectedValue = TryDereferenceAndUnboxValue(value, out inspectedValue);
            nint typeSource = hasInspectedValue ? inspectedValue : value;
            ManagedValueDisplay immediate = CorDebugValueFormatter.Format(typeSource);
            value2 = ComAbi.QueryInterface(typeSource, ICorDebugValue2Abi.InterfaceId);
            unsafe
            {
                nint* exactTypeAddress = &exactType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
                exactType = RequirePointer(
                    Volatile.Read(ref *exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
            }

            string type = FormatRuntimeType(exactType, depth: 0, out uint elementType);
            if (elementType == 0x11 &&
                hasInspectedValue &&
                TryFormatEnumValue(inspectedValue, exactType, out string enumDisplay))
            {
                return new ManagedValueDisplay(enumDisplay, type);
            }

            if (elementType == 0x11 && IsNullableType(exactType) && hasInspectedValue)
            {
                return new ManagedValueDisplay(
                    FormatNullableValue(inspectedValue, exactType),
                    type);
            }

            string display = elementType switch
            {
                0x11 or 0x12 => $"{{{type}}}",
                0x14 or 0x1d when hasInspectedValue => FormatArrayValue(inspectedValue, type),
                _ => immediate.Value
            };
            return new ManagedValueDisplay(display, type);
        }
        finally
        {
            if (exactType != 0)
            {
                _ = ComAbi.Release(exactType);
            }

            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
            }

            if (inspectedValue != 0)
            {
                _ = ComAbi.Release(inspectedValue);
            }
        }
    }

    private unsafe string FormatRuntimeType(nint type, int depth, out uint elementType)
    {
        if (depth >= MaximumRuntimeTypeDepth)
        {
            throw new InvalidOperationException(
                $"The runtime type exceeds the supported depth of {MaximumRuntimeTypeDepth}.");
        }

        uint runtimeElementType = 0;
        uint* elementTypeAddress = &runtimeElementType;
        var api = new ICorDebugTypeAbi(type);
        CorDebugHResult.ThrowIfFailed(
            api.GetType((nint)elementTypeAddress),
            "ICorDebugType.GetType");
        elementType = Volatile.Read(ref *elementTypeAddress);
        return elementType switch
        {
            0x01 => "void",
            0x02 => "bool",
            0x03 => "char",
            0x04 => "sbyte",
            0x05 => "byte",
            0x06 => "short",
            0x07 => "ushort",
            0x08 => "int",
            0x09 => "uint",
            0x0a => "long",
            0x0b => "ulong",
            0x0c => "float",
            0x0d => "double",
            0x0e => "string",
            0x0f => $"{FormatFirstTypeParameter(api, depth)}*",
            0x10 => $"{FormatFirstTypeParameter(api, depth)}&",
            0x11 or 0x12 => FormatNamedRuntimeType(type, depth),
            0x14 => FormatArrayType(api, depth),
            0x16 => "typed-reference",
            0x18 => "nint",
            0x19 => "nuint",
            0x1b => "delegate*",
            0x1c => "object",
            0x1d => $"{FormatFirstTypeParameter(api, depth)}[]",
            _ => $"element-type 0x{elementType:X2}"
        };
    }

    private string FormatNamedRuntimeType(nint type, int depth)
    {
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            uint typeToken = GetClassToken(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(typeToken & 0x00FFFFFF)));
            string name = GetMetadataTypeName(metadata, typeHandle);
            List<string> arguments = FormatRuntimeTypeArguments(type, depth);
            if (string.Equals(name, "System.Nullable`1", StringComparison.Ordinal) &&
                arguments.Count == 1)
            {
                return $"{arguments[0]}?";
            }

            if (name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) &&
                arguments.Count > 0)
            {
                return $"({string.Join(", ", arguments)})";
            }

            string displayName = RemoveGenericArity(name);
            return arguments.Count == 0
                ? displayName
                : $"{displayName}<{string.Join(", ", arguments)}>";
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
        }
    }

    private unsafe List<string> FormatRuntimeTypeArguments(nint type, int depth)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(type).EnumerateTypeParameters((nint)enumeratorAddress),
                "ICorDebugType.EnumerateTypeParameters");
            enumerator = RequirePointer(
                Volatile.Read(ref *enumeratorAddress),
                "ICorDebugType.EnumerateTypeParameters");
            var result = new List<string>();
            var values = new ICorDebugTypeEnumAbi(enumerator);
            for (int index = 0; index < MaximumRuntimeTypeArgumentCount; index++)
            {
                nint argument = 0;
                uint fetched = 0;
                nint* argumentAddress = &argument;
                uint* fetchedAddress = &fetched;
                CorDebugHResult.ThrowIfFailed(
                    values.Next(1, (nint)argumentAddress, (nint)fetchedAddress),
                    "ICorDebugTypeEnum.Next");
                argument = Volatile.Read(ref *argumentAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    return result;
                }

                try
                {
                    result.Add(FormatRuntimeType(argument, depth + 1, out _));
                }
                finally
                {
                    if (argument != 0)
                    {
                        _ = ComAbi.Release(argument);
                    }
                }
            }

            throw new InvalidOperationException(
                $"The runtime type exceeds the generic argument limit of " +
                $"{MaximumRuntimeTypeArgumentCount}.");
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }
        }
    }

    private unsafe string FormatFirstTypeParameter(ICorDebugTypeAbi type, int depth)
    {
        nint parameter = 0;
        try
        {
            nint* parameterAddress = &parameter;
            CorDebugHResult.ThrowIfFailed(
                type.GetFirstTypeParameter((nint)parameterAddress),
                "ICorDebugType.GetFirstTypeParameter");
            parameter = RequirePointer(
                Volatile.Read(ref *parameterAddress),
                "ICorDebugType.GetFirstTypeParameter");
            return FormatRuntimeType(parameter, depth + 1, out _);
        }
        finally
        {
            if (parameter != 0)
            {
                _ = ComAbi.Release(parameter);
            }
        }
    }

    private unsafe string FormatArrayType(ICorDebugTypeAbi type, int depth)
    {
        uint rank = 0;
        uint* rankAddress = &rank;
        CorDebugHResult.ThrowIfFailed(type.GetRank((nint)rankAddress), "ICorDebugType.GetRank");
        rank = Volatile.Read(ref *rankAddress);
        return $"{FormatFirstTypeParameter(type, depth)}[{new string(',', checked((int)rank - 1))}]";
    }

    private static string FormatArrayValue(nint value, string type)
    {
        nint array = 0;
        try
        {
            array = ComAbi.QueryInterface(value, ICorDebugArrayValueAbi.InterfaceId);
            var api = new ICorDebugArrayValueAbi(array);
            uint rank = GetArrayRank(api);
            uint[] dimensions = GetArrayDimensions(api, rank);
            int bracket = type.LastIndexOf('[');
            return bracket < 0
                ? $"{{{type}}}"
                : $"{{{type[..bracket]}[{string.Join(", ", dimensions)}]}}";
        }
        finally
        {
            if (array != 0)
            {
                _ = ComAbi.Release(array);
            }
        }
    }

    private unsafe bool IsNullableType(nint type)
    {
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            return metadata.StringComparer.Equals(definition.Namespace, "System") &&
                metadata.StringComparer.Equals(definition.Name, "Nullable`1");
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
        }
    }

    private string FormatNullableValue(nint value, nint type)
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
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            foreach (FieldDefinitionHandle fieldHandle in metadata.GetTypeDefinition(handle).GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                string name = metadata.GetString(field.Name);
                if (!string.Equals(name, "hasValue", StringComparison.Ordinal) &&
                    !string.Equals(name, "value", StringComparison.Ordinal))
                {
                    continue;
                }

                nint fieldValue = GetObjectFieldValue(
                    instance,
                    runtimeClass,
                    checked((uint)MetadataTokens.GetToken(fieldHandle)));
                if (string.Equals(name, "hasValue", StringComparison.Ordinal))
                {
                    hasValue = fieldValue;
                }
                else
                {
                    containedValue = fieldValue;
                }
            }

            if (hasValue == 0 || containedValue == 0)
            {
                throw new InvalidOperationException(
                    "System.Nullable<T> does not expose its required runtime fields.");
            }

            return string.Equals(
                CorDebugValueFormatter.Format(hasValue).Value,
                "true",
                StringComparison.Ordinal)
                    ? FormatRuntimeValue(containedValue).Value
                    : "null";
        }
        finally
        {
            if (containedValue != 0)
            {
                _ = ComAbi.Release(containedValue);
            }

            if (hasValue != 0)
            {
                _ = ComAbi.Release(hasValue);
            }

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

    private PEReader OpenRuntimeModule(nint module) => _sourceBreakpoints
        .FindModule(module)
        ?.OpenPeReader() ?? new PEReader(new FileStream(
            CorDebugModulePath.Get(module),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete));

    private static string GetMetadataTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition definition = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetMetadataTypeName(metadata, declaringType)}.{name}";
        }

        string @namespace = metadata.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string RemoveGenericArity(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        for (int index = 0; index < name.Length; index++)
        {
            if (name[index] != '`')
            {
                result.Append(name[index]);
                continue;
            }

            index++;
            while (index < name.Length && char.IsAsciiDigit(name[index]))
            {
                index++;
            }

            index--;
        }

        return result.ToString();
    }
}
