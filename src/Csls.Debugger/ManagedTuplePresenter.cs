using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats and expands structurally compatible managed ValueTuple values.
/// </summary>
internal sealed class ManagedTuplePresenter
{
    private const int MaximumTupleCardinality = 4 * 1024;
    private const int MaximumTupleDepth = 64;
    private const int TupleRestPosition = 8;
    private readonly IManagedObjectExpansionServices _services;
    private readonly Func<nint, int, string> _formatType;

    /// <summary>
    /// Creates tuple presentation over generation-owned runtime services.
    /// </summary>
    /// <param name="services">The runtime operations used to format and retain values.</param>
    /// <param name="formatType">The exact runtime type formatter for tuple elements.</param>
    internal ManagedTuplePresenter(
        IManagedObjectExpansionServices services,
        Func<nint, int, string> formatType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(formatType);
        _services = services;
        _formatType = formatType;
    }

    /// <summary>
    /// Formats one compatible runtime tuple type with flattened Rest storage.
    /// </summary>
    /// <param name="type">The exact ICorDebugType pointer.</param>
    /// <param name="depth">The current exact-type formatting depth.</param>
    /// <param name="display">Receives the tuple-syntax type display.</param>
    /// <returns>True when the runtime type is a compatible tuple with at least two elements.</returns>
    internal bool TryFormatType(nint type, int depth, out string display)
    {
        if (!TryGetCardinality(type, recursionDepth: 0, out int cardinality) ||
            cardinality <= 1)
        {
            display = string.Empty;
            return false;
        }

        List<string> elements = FormatTypeElements(type, depth, cardinality);
        display = $"({string.Join(", ", elements)})";
        return true;
    }

    /// <summary>
    /// Formats one compatible runtime tuple value without executing target code.
    /// </summary>
    /// <param name="value">The dereferenced and unboxed ICorDebugValue pointer.</param>
    /// <param name="exactType">The exact ICorDebugType pointer.</param>
    /// <param name="debuggerDisplayDepth">The current debugger-display recursion depth.</param>
    /// <param name="display">Receives the tuple-syntax value display.</param>
    /// <returns>True when the runtime value has a compatible tuple shape.</returns>
    internal bool TryFormatValue(
        nint value,
        nint exactType,
        int debuggerDisplayDepth,
        out string display)
    {
        if (!TryGetCardinality(exactType, recursionDepth: 0, out int cardinality) ||
            cardinality <= 1)
        {
            display = string.Empty;
            return false;
        }

        var elements = new List<string>(cardinality);
        VisitValues(
            value,
            exactType,
            cardinality,
            (fieldValue, _, _, _) => elements.Add(_services.FormatRuntimeValue(
                fieldValue,
                debuggerDisplayDepth + 1).Value),
            state: 0);
        display = $"({string.Join(", ", elements)})";
        return true;
    }

    /// <summary>
    /// Expands one logical page from a compatible runtime tuple value.
    /// </summary>
    /// <param name="value">The dereferenced and unboxed ICorDebugValue pointer.</param>
    /// <param name="parentEvaluateName">The optional source expression for the tuple.</param>
    /// <param name="frameId">The optional generation-owned frame identifier.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="start">The zero-based first logical child.</param>
    /// <param name="count">The maximum count, or zero for every remaining child.</param>
    /// <param name="result">Receives the tuple page.</param>
    /// <returns>True when the runtime value has a compatible tuple shape.</returns>
    internal bool TryExpand(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        out List<DebugVariableInfo> result)
    {
        nint exactType = 0;
        try
        {
            exactType = GetExactType(value);
            if (!TryGetCardinality(exactType, recursionDepth: 0, out int cardinality) ||
                cardinality <= 1)
            {
                result = [];
                return false;
            }

            result = [];
            VisitValues(
                value,
                exactType,
                cardinality,
                static (fieldValue, logicalIndex, evaluateName, state) =>
                {
                    (ManagedTuplePresenter presenter,
                        List<DebugVariableInfo> values,
                        int? frame,
                        DebugStopGeneration stop,
                        int first,
                        int maximum) = state;
                    if (logicalIndex < first ||
                        (maximum > 0 && values.Count >= maximum))
                    {
                        return;
                    }

                    ManagedValueDisplay fieldDisplay = presenter._services.FormatRuntimeValue(
                        fieldValue,
                        debuggerDisplayDepth: 0);
                    ManagedValueReferences references = presenter._services.RetainValue(
                        fieldValue,
                        stop,
                        evaluateName,
                        frame,
                        ManagedValueView.Default);
                    string logicalName = $"Item{logicalIndex + 1}";
                    values.Add(new DebugVariableInfo(
                        fieldDisplay.Name ?? logicalName,
                        fieldDisplay.Value,
                        fieldDisplay.Type,
                        references.VariablesReference,
                        references.MemoryReference,
                        evaluateName));
                },
                (this, result, frameId, generation, start, count),
                parentEvaluateName);

            if (cardinality > TupleRestPosition - 1 &&
                cardinality >= start &&
                (count == 0 || result.Count < count))
            {
                ManagedValueDisplay rawDisplay = _services.FormatRuntimeValue(
                    value,
                    debuggerDisplayDepth: 0);
                ManagedValueReferences rawReferences = _services.RetainValue(
                    value,
                    generation,
                    parentEvaluateName,
                    frameId,
                    ManagedValueView.Raw);
                result.Add(new DebugVariableInfo(
                    "Raw View",
                    rawDisplay.Value,
                    rawDisplay.Type,
                    rawReferences.VariablesReference,
                    rawReferences.MemoryReference,
                    EvaluateName: null,
                    DebugVariablePresentationKind.Virtual));
            }

            return true;
        }
        finally
        {
            if (exactType != 0)
            {
                _ = ComAbi.Release(exactType);
            }
        }
    }

    private List<string> FormatTypeElements(nint type, int depth, int cardinality)
    {
        var result = new List<string>(cardinality);
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
                        result.Add(_formatType(arguments[index], depth + 1));
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
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }
        }
    }

    private void VisitValues<TState>(
        nint value,
        nint exactType,
        int cardinality,
        Action<nint, int, string?, TState> visitor,
        TState state,
        string? parentEvaluateName = null)
    {
        nint currentValue = Retain(value);
        nint currentType = Retain(exactType);
        try
        {
            int remaining = cardinality;
            int logicalIndex = 0;
            string? layerEvaluateName = parentEvaluateName;
            for (int tupleDepth = 0; tupleDepth < MaximumTupleDepth; tupleDepth++)
            {
                int fieldCount = Math.Min(remaining, TupleRestPosition - 1);
                for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                {
                    string physicalName = $"Item{fieldIndex + 1}";
                    nint fieldValue = GetFieldValue(currentValue, currentType, physicalName);
                    try
                    {
                        visitor(
                            fieldValue,
                            logicalIndex,
                            ManagedExpressionName.CreateMember(
                                layerEvaluateName,
                                physicalName),
                            state);
                    }
                    finally
                    {
                        _ = ComAbi.Release(fieldValue);
                    }

                    logicalIndex++;
                }

                remaining -= fieldCount;
                if (remaining == 0)
                {
                    return;
                }

                nint restValue = GetFieldValue(currentValue, currentType, "Rest");
                nint restType = 0;
                try
                {
                    restType = GetExactType(restValue);
                    _ = ComAbi.Release(currentValue);
                    currentValue = restValue;
                    restValue = 0;
                    _ = ComAbi.Release(currentType);
                    currentType = restType;
                    restType = 0;
                    layerEvaluateName = ManagedExpressionName.CreateMember(
                        layerEvaluateName,
                        "Rest");
                }
                finally
                {
                    if (restType != 0)
                    {
                        _ = ComAbi.Release(restType);
                    }

                    if (restValue != 0)
                    {
                        _ = ComAbi.Release(restValue);
                    }
                }
            }

            throw new InvalidOperationException(
                $"The tuple exceeds the supported nesting depth of {MaximumTupleDepth}.");
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }

            if (currentValue != 0)
            {
                _ = ComAbi.Release(currentValue);
            }
        }
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
                    $"The tuple exceeds the supported cardinality of " +
                    $"{MaximumTupleCardinality}.");
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
        unsafe
        {
            uint elementType = 0;
            uint* elementTypeAddress = &elementType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(type).GetType((nint)elementTypeAddress),
                "ICorDebugType.GetType");
            if (Volatile.Read(ref *elementTypeAddress) != 0x11)
            {
                arity = 0;
                return false;
            }
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
        var fields = new HashSet<string>(StringComparer.Ordinal);
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
            var result = new List<nint>(TupleRestPosition);
            var values = new ICorDebugTypeEnumAbi(enumerator);
            for (int index = 0; index <= TupleRestPosition; index++)
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
            throw new BadImageFormatException(
                $"A ValueTuple type cannot have more than {TupleRestPosition} arguments.");
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }
        }
    }

    private nint GetFieldValue(nint value, nint type, string fieldName)
    {
        nint instance = 0;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader peReader = _services.OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            foreach (FieldDefinitionHandle fieldHandle in metadata
                .GetTypeDefinition(handle)
                .GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) == 0 &&
                    string.Equals(
                        metadata.GetString(field.Name),
                        fieldName,
                        StringComparison.Ordinal))
                {
                    return GetObjectFieldValue(
                        instance,
                        runtimeClass,
                        checked((uint)MetadataTokens.GetToken(fieldHandle)));
                }
            }

            throw new BadImageFormatException(
                $"Compatible tuple type is missing field '{fieldName}'.");
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

    private static unsafe nint GetExactType(nint value)
    {
        nint value2 = 0;
        try
        {
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            nint exactType = 0;
            nint* exactTypeAddress = &exactType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            return RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");
        }
        finally
        {
            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
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
