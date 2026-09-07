using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats exact managed types using the owning process's module metadata.
/// </summary>
internal sealed class ManagedRuntimeTypeFormatter
{
    private const int MaximumRuntimeTypeDepth = 64;
    private const int MaximumRuntimeTypeArgumentCount = 256;
    private readonly Func<nint, PEReader> _openModule;
    private readonly ManagedTupleTypeShape _typeShape;

    /// <summary>
    /// Binds type inspection to the owning process's metadata reader.
    /// </summary>
    internal ManagedRuntimeTypeFormatter(Func<nint, PEReader> openModule)
    {
        ArgumentNullException.ThrowIfNull(openModule);
        _openModule = openModule;
        _typeShape = new ManagedTupleTypeShape(openModule);
    }

    /// <summary>
    /// Formats the exact runtime type of a borrowed live or captured value.
    /// </summary>
    internal string FormatValueType(nint value) => FormatValueType(value, arrayElement: false);

    /// <summary>
    /// Formats the declared element type of a borrowed array independently of its captured element storage.
    /// </summary>
    internal string FormatArrayElementType(nint value) => FormatValueType(value, arrayElement: true);

    private unsafe string FormatValueType(nint value, bool arrayElement)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint type = 0;
        try
        {
            int result = new ICorDebugValue2Abi(value2).GetExactType((nint)(&type));
            type = Volatile.Read(ref type);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugValue2.GetExactType");
            type = RequirePointer(type, "ICorDebugValue2.GetExactType");
            return arrayElement
                ? FormatFirstTypeParameter(type, new ICorDebugTypeAbi(type), 0, null)
                : Format(type, 0, null, out _, out _);
        }
        finally
        {
            Release(type);
            Release(value2);
        }
    }

    /// <summary>
    /// Formats a borrowed exact runtime type using bounded structural and metadata inspection.
    /// </summary>
    internal unsafe string Format(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        out uint elementType,
        out uint? intrinsicElementType)
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
        intrinsicElementType = null;
        if (elementType is 0x11 or 0x12)
        {
            return FormatNamedRuntimeType(type, depth, tupleCustomTypeInfo, out intrinsicElementType);
        }

        return FormatPrimitiveRuntimeType(elementType) ?? (elementType switch
        {
            0x0f => $"{FormatFirstTypeParameter(type, api, depth, tupleCustomTypeInfo)}*",
            0x10 => $"{FormatFirstTypeParameter(type, api, depth, tupleCustomTypeInfo)}&",
            0x14 => FormatArrayType(type, api, depth, tupleCustomTypeInfo),
            0x1b => "delegate*",
            0x1d => $"{FormatFirstTypeParameter(type, api, depth, tupleCustomTypeInfo)}[]",
            _ => $"element-type 0x{elementType:X2}"
        });
    }

    /// <summary>
    /// Gets the source display for an intrinsic CLR element kind.
    /// </summary>
    internal static string? FormatPrimitiveRuntimeType(uint elementType) => elementType switch
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
        0x16 => "typed-reference",
        0x18 => "nint",
        0x19 => "nuint",
        0x1c => "object",
        _ => null
    };

    private string FormatNamedRuntimeType(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        out uint? intrinsicElementType)
    {
        intrinsicElementType = null;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            uint typeToken = GetClassToken(runtimeClass);
            using PEReader peReader = _openModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(typeToken & 0x00FFFFFF)));
            string name = GetMetadataTypeName(metadata, typeHandle);
            if (ManagedBoundTypeSystem.GetIntrinsicElementType(name) is uint intrinsic &&
                IsCoreLibraryDefinition(type, module))
            {
                intrinsicElementType = intrinsic;
                return FormatPrimitiveRuntimeType(intrinsic)
                    ?? throw new InvalidOperationException("An intrinsic type has no display name.");
            }

            if (name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) &&
                TryFormatTupleType(
                    type,
                    depth,
                    tupleCustomTypeInfo,
                    out string tupleType))
            {
                return tupleType;
            }

            List<string> arguments = FormatRuntimeTypeArguments(
                type,
                depth,
                tupleCustomTypeInfo);
            if (string.Equals(name, "System.Nullable`1", StringComparison.Ordinal) &&
                arguments.Count == 1 && ManagedNullableTypeIdentity.IsNullableType(type, _openModule))
            {
                return $"{arguments[0]}?";
            }

            string displayName = RemoveGenericArity(name);
            if (string.Equals(displayName, "System.Decimal", StringComparison.Ordinal) &&
                IsCoreLibraryDefinition(type, module))
            {
                return "decimal";
            }

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

    /// <summary>
    /// Establishes intrinsic type identity against its runtime root module.
    /// </summary>
    internal bool IsCoreLibraryDefinition(nint type)
    {
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            return IsCoreLibraryDefinition(type, module);
        }
        finally
        {
            Release(module);
            Release(runtimeClass);
        }
    }

    private unsafe bool IsCoreLibraryDefinition(nint type, nint module)
    {
        nint current = type;
        _ = ComAbi.AddRef(current);
        nint rootClass = 0;
        nint rootModule = 0;
        try
        {
            for (int depth = 0; depth < MaximumRuntimeTypeDepth; depth++)
            {
                nint parent = 0;
                nint* parentAddress = &parent;
                CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(current).GetBase((nint)parentAddress),
                    "ICorDebugType.GetBase");
                parent = Volatile.Read(ref *parentAddress);
                if (parent == 0)
                {
                    rootClass = GetRuntimeTypeClass(current);
                    rootModule = GetClassModule(rootClass);
                    nint identity = ComAbi.GetIdentity(module);
                    nint rootIdentity = 0;
                    try
                    {
                        rootIdentity = ComAbi.GetIdentity(rootModule);
                        if (identity != rootIdentity)
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Release(rootIdentity);
                        Release(identity);
                    }

                    using PEReader pe = _openModule(rootModule);
                    MetadataReader metadata = pe.GetMetadataReader();
                    var handle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(checked((int)GetClassToken(rootClass)));
                    TypeDefinition definition = metadata.GetTypeDefinition(handle);
                    return (definition.Attributes & System.Reflection.TypeAttributes.Interface) == 0 &&
                        GetMetadataTypeName(metadata, handle) == "System.Object";
                }

                _ = ComAbi.Release(current);
                current = parent;
            }

            throw new InvalidOperationException("The runtime type hierarchy exceeds the supported depth.");
        }
        finally
        {
            Release(rootModule);
            Release(rootClass);
            Release(current);
        }
    }

    private unsafe List<string> FormatRuntimeTypeArguments(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
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
                    result.Add(Format(
                        argument,
                        depth + 1,
                        _typeShape.GetTypeArgumentCustomTypeInfo(
                            type,
                            tupleCustomTypeInfo,
                            index),
                        out _, out _));
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

    private unsafe string FormatFirstTypeParameter(
        nint exactType,
        ICorDebugTypeAbi type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
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
            return Format(
                parameter,
                depth + 1,
                _typeShape.GetTypeArgumentCustomTypeInfo(
                    exactType,
                    tupleCustomTypeInfo,
                    argumentIndex: 0),
                out _, out _);
        }
        finally
        {
            if (parameter != 0)
            {
                _ = ComAbi.Release(parameter);
            }
        }
    }

    private unsafe string FormatArrayType(
        nint exactType,
        ICorDebugTypeAbi type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
    {
        uint rank = 0;
        uint* rankAddress = &rank;
        CorDebugHResult.ThrowIfFailed(type.GetRank((nint)rankAddress), "ICorDebugType.GetRank");
        rank = Volatile.Read(ref *rankAddress);
        if (rank is < 1 or > 32)
        {
            throw new InvalidOperationException("The runtime array rank is outside the supported range of 1 through 32.");
        }
        string dimensions = rank == 1 ? "*" : new string(',', checked((int)rank - 1));
        return $"{FormatFirstTypeParameter(exactType, type, depth, tupleCustomTypeInfo)}" +
            $"[{dimensions}]";
    }

    /// <summary>
    /// Formats one compatible runtime tuple type with flattened Rest storage.
    /// </summary>
    /// <param name="type">The exact ICorDebugType pointer.</param>
    /// <param name="depth">The current exact-type formatting depth.</param>
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="display">Receives the tuple-syntax type display.</param>
    /// <returns>True when the runtime type is a compatible tuple with at least two elements.</returns>
    private bool TryFormatTupleType(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        out string display)
    {
        if (!_typeShape.TryCreateProjection(
            type,
            customTypeInfo,
            out ManagedTupleTypeProjection projection))
        {
            display = string.Empty;
            return false;
        }

        IReadOnlyList<nint> elementTypes = ManagedTupleTypeShape.GetLogicalElementTypes(
            type,
            projection.ElementNames.Count);
        try
        {
            string[] elements = new string[elementTypes.Count];
            for (int index = 0; index < elementTypes.Count; index++)
            {
                string typeDisplay = Format(
                    elementTypes[index],
                    depth + 1,
                    projection.ElementCustomTypeInfo[index], out _, out _);
                string logicalName = projection.ElementNames[index];
                elements[index] = string.Equals(
                    logicalName,
                    $"Item{index + 1}",
                    StringComparison.Ordinal)
                        ? typeDisplay
                        : $"{typeDisplay} {logicalName}";
            }

            display = $"({string.Join(", ", elements)})";
            return true;
        }
        finally
        {
            foreach (nint elementType in elementTypes)
            {
                Release(elementType);
            }
        }
    }

    private static string GetMetadataTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle,
        int depth = 0)
    {
        if (depth >= MaximumRuntimeTypeDepth)
        {
            throw new BadImageFormatException("The metadata type nesting exceeds the supported depth.");
        }
        TypeDefinition definition = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetMetadataTypeName(metadata, declaringType, depth + 1)}.{name}";
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

    private static unsafe nint GetRuntimeTypeClass(nint type)
    {
        nint result = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetClass((nint)(&result)), "ICorDebugType.GetClass");
        return RequirePointer(Volatile.Read(ref result), "ICorDebugType.GetClass");
    }

    private static unsafe nint GetClassModule(nint runtimeClass)
    {
        nint result = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetModule((nint)(&result)), "ICorDebugClass.GetModule");
        return RequirePointer(Volatile.Read(ref result), "ICorDebugClass.GetModule");
    }

    private static unsafe uint GetClassToken(nint runtimeClass)
    {
        uint result = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetToken((nint)(&result)), "ICorDebugClass.GetToken");
        return Volatile.Read(ref result);
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer : throw new InvalidOperationException($"{operation} returned no runtime type.");

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
