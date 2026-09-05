using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Maps runtime type constructions to logical ValueTuple elements and name transforms.
/// </summary>
internal sealed class ManagedTupleTypeShape
{
    private const int MaximumRuntimeTypeArgumentCount = 256;
    private const int MaximumTransformNameCount = 64 * 1024;
    private const int MaximumTupleCardinality = 4 * 1024;
    private const int MaximumTupleDepth = 64;
    private const int TupleRestPosition = 8;
    private readonly IManagedObjectExpansionServices _services;

    /// <summary>
    /// Creates tuple type mapping over loaded runtime modules.
    /// </summary>
    /// <param name="services">The runtime module access used to validate tuple types.</param>
    internal ManagedTupleTypeShape(IManagedObjectExpansionServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>
    /// Creates a validated projection for one compatible runtime tuple type.
    /// </summary>
    /// <param name="type">The exact ICorDebugType pointer.</param>
    /// <param name="customTypeInfo">The optional transforms for the declared type use.</param>
    /// <param name="projection">Receives logical element names and nested transforms.</param>
    /// <returns>True when the type is a compatible tuple with at least two elements.</returns>
    internal bool TryCreateProjection(
        nint type,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        out ManagedTupleTypeProjection projection)
    {
        if (!TryGetCardinality(type, recursionDepth: 0, out int cardinality) ||
            cardinality <= 1)
        {
            projection = null!;
            return false;
        }

        int expectedNameCount = GetTransformNameCount(type, recursionDepth: 0);
        var nested = new ManagedTupleCustomTypeInfo?[cardinality];
        bool transformsAreValid = customTypeInfo?.TransformNames.Count == expectedNameCount &&
            TryMapElementTransforms(type, cardinality, customTypeInfo, nested);
        string[] names = new string[cardinality];
        bool hasAuthoredElementNames = false;
        for (int index = 0; index < cardinality; index++)
        {
            string? authoredName = transformsAreValid
                ? customTypeInfo!.TransformNames[index]
                : null;
            names[index] = string.IsNullOrEmpty(authoredName)
                ? $"Item{index + 1}"
                : authoredName;
            hasAuthoredElementNames |= !string.IsNullOrEmpty(authoredName);
        }

        if (!transformsAreValid)
        {
            Array.Clear(nested);
        }

        projection = new ManagedTupleTypeProjection(
            names,
            nested,
            hasAuthoredElementNames);
        return true;
    }

    /// <summary>
    /// Gets retained exact types for every flattened logical tuple element.
    /// </summary>
    /// <param name="type">The exact compatible tuple type.</param>
    /// <param name="cardinality">The previously validated logical cardinality.</param>
    /// <returns>Retained ICorDebugType pointers that the caller must release.</returns>
    internal static IReadOnlyList<nint> GetLogicalElementTypes(nint type, int cardinality)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cardinality);
        List<nint> result = new(cardinality);
        nint currentType = Retain(type);
        try
        {
            int remaining = cardinality;
            for (int tupleDepth = 0; tupleDepth < MaximumTupleDepth; tupleDepth++)
            {
                List<nint> arguments = EnumerateTypeArguments(currentType);
                try
                {
                    int fieldCount = Math.Min(remaining, TupleRestPosition - 1);
                    for (int index = 0; index < fieldCount; index++)
                    {
                        result.Add(Retain(arguments[index]));
                    }

                    remaining -= fieldCount;
                    if (remaining == 0)
                    {
                        return result;
                    }

                    nint restType = Retain(arguments[TupleRestPosition - 1]);
                    _ = ComAbi.Release(currentType);
                    currentType = restType;
                }
                finally
                {
                    ReleaseAll(arguments);
                }
            }

            throw new InvalidOperationException(
                $"The tuple exceeds the supported nesting depth of {MaximumTupleDepth}.");
        }
        catch
        {
            ReleaseAll(result);
            throw;
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }
        }
    }

    /// <summary>
    /// Projects tuple metadata onto one ordinary runtime type argument.
    /// </summary>
    /// <param name="type">The exact non-tuple runtime type.</param>
    /// <param name="customTypeInfo">The optional transforms for the complete type.</param>
    /// <param name="argumentIndex">The zero-based runtime type argument.</param>
    /// <returns>Transforms for the selected argument, or null when none are usable.</returns>
    internal ManagedTupleCustomTypeInfo? GetTypeArgumentCustomTypeInfo(
        nint type,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        int argumentIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(argumentIndex);
        if (customTypeInfo is null ||
            TryGetCardinality(type, recursionDepth: 0, out int cardinality) &&
            cardinality > 1)
        {
            return null;
        }

        List<nint> arguments = EnumerateTypeArguments(type);
        try
        {
            if (argumentIndex >= arguments.Count)
            {
                return null;
            }

            int offset = 0;
            int selectedCount = 0;
            int totalCount = 0;
            for (int index = 0; index < arguments.Count; index++)
            {
                int count = GetTransformNameCount(arguments[index], recursionDepth: 0);
                if (index < argumentIndex)
                {
                    offset = checked(offset + count);
                }
                else if (index == argumentIndex)
                {
                    selectedCount = count;
                }

                totalCount = checked(totalCount + count);
            }

            return totalCount == customTypeInfo.TransformNames.Count
                ? customTypeInfo.Slice(offset, selectedCount)
                : null;
        }
        finally
        {
            ReleaseAll(arguments);
        }
    }

    /// <summary>
    /// Projects declaration names onto a field whose complete signature is a containing type parameter.
    /// </summary>
    /// <param name="type">The exact runtime type declaring the field.</param>
    /// <param name="metadata">The declaring module's metadata.</param>
    /// <param name="field">The field whose authored or substituted tuple names are requested.</param>
    /// <param name="customTypeInfo">The optional transforms for the complete containing type.</param>
    /// <returns>Authored field names or transforms for its direct generic parameter.</returns>
    internal ManagedTupleCustomTypeInfo? GetFieldCustomTypeInfo(
        nint type, MetadataReader metadata, FieldDefinition field, ManagedTupleCustomTypeInfo? customTypeInfo)
    {
        ManagedTupleCustomTypeInfo? declared = ManagedTupleElementNameReader.ReadAttribute(
            metadata, field.GetCustomAttributes());
        if (declared is not null || customTypeInfo is null)
        {
            return declared;
        }

        BlobReader signature = metadata.GetBlobReader(field.Signature);
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Field ||
            signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeParameter)
        {
            return null;
        }

        int index = signature.ReadCompressedInteger();
        return signature.RemainingBytes == 0
            ? GetTypeArgumentCustomTypeInfo(type, customTypeInfo, index)
            : null;
    }

    /// <summary>
    /// Gets the logical cardinality of one compatible runtime tuple type.
    /// </summary>
    /// <param name="type">The exact ICorDebugType pointer.</param>
    /// <param name="cardinality">Receives the flattened cardinality.</param>
    /// <returns>True when the type has compatible ValueTuple storage.</returns>
    internal bool TryGetCardinality(nint type, out int cardinality) =>
        TryGetCardinality(type, recursionDepth: 0, out cardinality);

    private int GetTransformNameCount(nint type, int recursionDepth)
    {
        if (recursionDepth >= MaximumTupleDepth)
        {
            throw new InvalidOperationException(
                $"The tuple-name transform exceeds the supported depth of {MaximumTupleDepth}.");
        }

        uint elementType = GetElementType(type);
        if (TryGetCardinality(type, recursionDepth, out int cardinality) &&
            cardinality > 1)
        {
            return GetTupleTransformNameCount(
                type,
                cardinality,
                recursionDepth);
        }

        int result = 0;
        if (!CanContainTypeArguments(elementType))
        {
            return result;
        }

        List<nint> arguments = EnumerateTypeArguments(type);
        try
        {
            foreach (nint argument in arguments)
            {
                result = checked(result + GetTransformNameCount(
                    argument,
                    recursionDepth + 1));
                if (result > MaximumTransformNameCount)
                {
                    throw new InvalidOperationException(
                        $"The tuple-name transform exceeds the supported count of " +
                        $"{MaximumTransformNameCount}.");
                }
            }

            return result;
        }
        finally
        {
            ReleaseAll(arguments);
        }
    }

    private int GetTupleTransformNameCount(
        nint type,
        int cardinality,
        int recursionDepth)
    {
        int result = cardinality;
        List<nint> arguments = EnumerateTypeArguments(type);
        try
        {
            int directElementCount = Math.Min(cardinality, TupleRestPosition - 1);
            for (int index = 0; index < directElementCount; index++)
            {
                result = AddTransformNameCount(
                    result,
                    GetTransformNameCount(arguments[index], recursionDepth + 1));
            }

            int remaining = cardinality - directElementCount;
            if (remaining == 0)
            {
                return result;
            }

            nint restType = arguments[TupleRestPosition - 1];
            if (!TryGetCardinality(
                    restType,
                    recursionDepth + 1,
                    out int restCardinality) ||
                restCardinality != remaining)
            {
                throw new InvalidOperationException(
                    "The tuple Rest type does not match its logical cardinality.");
            }

            return AddTransformNameCount(
                result,
                GetTupleTransformNameCount(
                    restType,
                    restCardinality,
                    recursionDepth + 1));
        }
        finally
        {
            ReleaseAll(arguments);
        }
    }

    private static int AddTransformNameCount(int left, int right)
    {
        int result = checked(left + right);
        if (result > MaximumTransformNameCount)
        {
            throw new InvalidOperationException(
                $"The tuple-name transform exceeds the supported count of " +
                $"{MaximumTransformNameCount}.");
        }

        return result;
    }

    private bool TryMapElementTransforms(
        nint type,
        int cardinality,
        ManagedTupleCustomTypeInfo customTypeInfo,
        ManagedTupleCustomTypeInfo?[] elementCustomTypeInfo)
    {
        int logicalIndex = 0;
        int offset = cardinality;
        int remaining = cardinality;
        nint currentType = Retain(type);
        try
        {
            for (int tupleDepth = 0; tupleDepth < MaximumTupleDepth; tupleDepth++)
            {
                List<nint> arguments = EnumerateTypeArguments(currentType);
                try
                {
                    int directElementCount = Math.Min(
                        remaining,
                        TupleRestPosition - 1);
                    for (int index = 0; index < directElementCount; index++)
                    {
                        int count = GetTransformNameCount(
                            arguments[index],
                            recursionDepth: tupleDepth + 1);
                        elementCustomTypeInfo[logicalIndex++] = customTypeInfo.Slice(
                            offset,
                            count);
                        offset = checked(offset + count);
                    }

                    remaining -= directElementCount;
                    if (remaining == 0)
                    {
                        return logicalIndex == cardinality &&
                            offset == customTypeInfo.TransformNames.Count;
                    }

                    nint restType = arguments[TupleRestPosition - 1];
                    if (remaining == 1)
                    {
                        if (!IsUnnamedTransformSegment(customTypeInfo, offset, count: 1))
                        {
                            return false;
                        }

                        offset = checked(offset + 1);
                        List<nint> restArguments = EnumerateTypeArguments(restType);
                        try
                        {
                            if (restArguments.Count != 1)
                            {
                                return false;
                            }

                            int count = GetTransformNameCount(
                                restArguments[0],
                                recursionDepth: tupleDepth + 1);
                            elementCustomTypeInfo[logicalIndex++] = customTypeInfo.Slice(
                                offset,
                                count);
                            offset = checked(offset + count);
                            return logicalIndex == cardinality &&
                                offset == customTypeInfo.TransformNames.Count;
                        }
                        finally
                        {
                            ReleaseAll(restArguments);
                        }
                    }

                    if (!TryGetCardinality(
                            restType,
                            recursionDepth: tupleDepth + 1,
                            out int restCardinality) ||
                        restCardinality != remaining ||
                        !IsUnnamedTransformSegment(customTypeInfo, offset, restCardinality))
                    {
                        return false;
                    }

                    offset = checked(offset + restCardinality);
                    nint nextType = Retain(restType);
                    _ = ComAbi.Release(currentType);
                    currentType = nextType;
                }
                finally
                {
                    ReleaseAll(arguments);
                }
            }

            return false;
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }
        }
    }

    private static bool IsUnnamedTransformSegment(
        ManagedTupleCustomTypeInfo customTypeInfo,
        int start,
        int count)
    {
        if (start < 0 ||
            count < 0 ||
            start > customTypeInfo.TransformNames.Count - count)
        {
            return false;
        }

        for (int index = start; index < start + count; index++)
        {
            if (!string.IsNullOrEmpty(customTypeInfo.TransformNames[index]))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetCardinality(nint type, int recursionDepth, out int cardinality)
    {
        if (recursionDepth >= MaximumTupleDepth)
        {
            throw new InvalidOperationException(
                $"The tuple exceeds the supported nesting depth of {MaximumTupleDepth}.");
        }

        if (!TryGetTupleArity(type, out int arity))
        {
            cardinality = 0;
            return false;
        }

        List<nint> arguments = EnumerateTypeArguments(type);
        try
        {
            if (arguments.Count != arity)
            {
                cardinality = 0;
                return false;
            }

            if (arity < TupleRestPosition)
            {
                cardinality = arity;
                return true;
            }

            if (!TryGetCardinality(
                arguments[TupleRestPosition - 1],
                recursionDepth + 1,
                out int restCardinality))
            {
                cardinality = 0;
                return false;
            }

            cardinality = checked(TupleRestPosition - 1 + restCardinality);
            if (cardinality > MaximumTupleCardinality)
            {
                throw new InvalidOperationException(
                    $"The tuple exceeds the supported cardinality of {MaximumTupleCardinality}.");
            }

            return true;
        }
        finally
        {
            ReleaseAll(arguments);
        }
    }

    private bool TryGetTupleArity(nint type, out int arity)
    {
        if (GetElementType(type) != 0x11)
        {
            arity = 0;
            return false;
        }

        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader peReader = _services.OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            if (!metadata.StringComparer.Equals(definition.Namespace, "System"))
            {
                arity = 0;
                return false;
            }

            string name = metadata.GetString(definition.Name);
            arity = name switch
            {
                "ValueTuple`1" => 1,
                "ValueTuple`2" => 2,
                "ValueTuple`3" => 3,
                "ValueTuple`4" => 4,
                "ValueTuple`5" => 5,
                "ValueTuple`6" => 6,
                "ValueTuple`7" => 7,
                "ValueTuple`8" => 8,
                _ => 0
            };
            return arity != 0 && HasExpectedFields(metadata, definition, arity);
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

    private static bool HasExpectedFields(
        MetadataReader metadata,
        TypeDefinition definition,
        int arity)
    {
        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (FieldDefinition field in definition
            .GetFields()
            .Select(metadata.GetFieldDefinition)
            .Where(field =>
                (field.Attributes & FieldAttributes.Static) == 0 &&
                (field.Attributes & FieldAttributes.FieldAccessMask) ==
                    FieldAttributes.Public))
        {
            _ = fields.Add(metadata.GetString(field.Name));
        }

        int itemCount = Math.Min(arity, TupleRestPosition - 1);
        for (int index = 1; index <= itemCount; index++)
        {
            if (!fields.Contains($"Item{index}"))
            {
                return false;
            }
        }

        return arity < TupleRestPosition || fields.Contains("Rest");
    }

    private static bool CanContainTypeArguments(uint elementType) => elementType is
        0x0f or 0x10 or 0x11 or 0x12 or 0x14 or 0x1d;

    private static unsafe uint GetElementType(nint type)
    {
        uint elementType = 0;
        uint* elementTypeAddress = &elementType;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugTypeAbi(type).GetType((nint)elementTypeAddress),
            "ICorDebugType.GetType");
        return Volatile.Read(ref *elementTypeAddress);
    }

    private static unsafe List<nint> EnumerateTypeArguments(nint type)
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
            List<nint> result = [];
            ICorDebugTypeEnumAbi values = new(enumerator);
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

                result.Add(RequirePointer(argument, "ICorDebugTypeEnum.Next"));
            }

            ReleaseAll(result);
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

    private static unsafe nint GetClassModule(nint runtimeClass)
    {
        nint module = 0;
        nint* moduleAddress = &module;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetModule((nint)moduleAddress),
            "ICorDebugClass.GetModule");
        return RequirePointer(
            Volatile.Read(ref *moduleAddress),
            "ICorDebugClass.GetModule");
    }

    private static unsafe uint GetClassToken(nint runtimeClass)
    {
        uint token = 0;
        uint* tokenAddress = &token;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenAddress),
            "ICorDebugClass.GetToken");
        return Volatile.Read(ref *tokenAddress);
    }

    private static nint Retain(nint value)
    {
        _ = ComAbi.AddRef(value);
        return value;
    }

    private static void ReleaseAll(IEnumerable<nint> values)
    {
        foreach (nint value in values)
        {
            _ = ComAbi.Release(value);
        }
    }

    private static nint RequirePointer(nint value, string operation) => value != 0
        ? value
        : throw new InvalidOperationException($"{operation} returned no object.");
}
