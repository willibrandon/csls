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
    private const int MaximumTupleDepth = 64;
    private const int TupleRestPosition = 8;
    private readonly IManagedObjectExpansionServices _services;
    private readonly ManagedTupleTypeShape _typeShape;
    private readonly Func<nint, int, ManagedTupleCustomTypeInfo?, string> _formatType;

    /// <summary>
    /// Creates tuple presentation over generation-owned runtime services.
    /// </summary>
    /// <param name="services">The runtime operations used to format and retain values.</param>
    /// <param name="typeShape">The runtime tuple type and transform mapper.</param>
    /// <param name="formatType">The exact runtime type formatter for tuple elements.</param>
    internal ManagedTuplePresenter(
        IManagedObjectExpansionServices services,
        ManagedTupleTypeShape typeShape,
        Func<nint, int, ManagedTupleCustomTypeInfo?, string> formatType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(typeShape);
        ArgumentNullException.ThrowIfNull(formatType);
        _services = services;
        _typeShape = typeShape;
        _formatType = formatType;
    }

    /// <summary>
    /// Formats one compatible runtime tuple type with flattened Rest storage.
    /// </summary>
    /// <param name="type">The exact ICorDebugType pointer.</param>
    /// <param name="depth">The current exact-type formatting depth.</param>
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="display">Receives the tuple-syntax type display.</param>
    /// <returns>True when the runtime type is a compatible tuple with at least two elements.</returns>
    internal bool TryFormatType(
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
                string typeDisplay = _formatType(
                    elementTypes[index],
                    depth + 1,
                    projection.ElementCustomTypeInfo[index]);
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
            ReleaseAll(elementTypes);
        }
    }

    /// <summary>
    /// Formats one compatible runtime tuple value without executing target code.
    /// </summary>
    /// <param name="value">The dereferenced and unboxed ICorDebugValue pointer.</param>
    /// <param name="exactType">The exact ICorDebugType pointer.</param>
    /// <param name="debuggerDisplayDepth">The current debugger-display recursion depth.</param>
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="display">Receives the tuple-syntax value display.</param>
    /// <returns>True when the runtime value has a compatible tuple shape.</returns>
    internal bool TryFormatValue(
        nint value,
        nint exactType,
        int debuggerDisplayDepth,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        out string display)
    {
        if (!_typeShape.TryCreateProjection(
            exactType,
            customTypeInfo,
            out ManagedTupleTypeProjection projection))
        {
            display = string.Empty;
            return false;
        }

        List<string> elements = new(projection.ElementNames.Count);
        VisitValues(
            value,
            exactType,
            projection,
            (fieldValue, _, _, fieldCustomTypeInfo, _, _) => elements.Add(
                _services.FormatRuntimeValue(
                    fieldValue,
                    debuggerDisplayDepth + 1,
                    fieldCustomTypeInfo).Value),
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
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="start">The zero-based first logical child.</param>
    /// <param name="count">The maximum count, or zero for every remaining child.</param>
    /// <param name="result">Receives the tuple page.</param>
    /// <param name="origin">The exact physical storage of the tuple value.</param>
    /// <param name="lifetime">The optional materialized snapshot owning retained descendants.</param>
    /// <returns>True when the runtime value has a compatible tuple shape.</returns>
    internal bool TryExpand(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        int start,
        int count,
        out List<DebugVariableInfo> result,
        ManagedValueOrigin? origin = null,
        ManagedResultsViewLifetime? lifetime = null)
    {
        nint exactType = 0;
        try
        {
            exactType = GetExactType(value);
            if (!_typeShape.TryCreateProjection(
                exactType,
                customTypeInfo,
                out ManagedTupleTypeProjection projection))
            {
                result = [];
                return false;
            }

            result = [];
            VisitValues(
                value,
                exactType,
                projection,
                static (fieldValue, logicalIndex, evaluateName, fieldCustomTypeInfo, fieldOrigin, state) =>
                {
                    (ManagedTuplePresenter presenter,
                        ManagedTupleTypeProjection tupleProjection,
                        List<DebugVariableInfo> values,
                        int? frame,
                        DebugStopGeneration stop,
                        int first,
                        int maximum,
                        ManagedResultsViewLifetime? snapshotLifetime) = state;
                    if (logicalIndex < first ||
                        (maximum > 0 && values.Count >= maximum))
                    {
                        return;
                    }

                    ManagedValueDisplay fieldDisplay = presenter._services.FormatRuntimeValue(
                        fieldValue,
                        debuggerDisplayDepth: 0,
                        fieldCustomTypeInfo);
                    ManagedValueReferences references = presenter._services.RetainValue(
                        fieldValue,
                        stop,
                        evaluateName,
                        frame,
                        ManagedValueView.Default,
                        fieldCustomTypeInfo,
                        fieldOrigin,
                        snapshotLifetime);
                    string logicalName = tupleProjection.ElementNames[logicalIndex];
                    values.Add(new DebugVariableInfo(
                        fieldDisplay.Name ?? logicalName,
                        fieldDisplay.Value,
                        fieldDisplay.Type,
                        references.VariablesReference,
                        references.MemoryReference,
                        evaluateName));
                },
                (this, projection, result, frameId, generation, start, count, lifetime),
                parentEvaluateName,
                origin);

            int cardinality = projection.ElementNames.Count;
            if ((cardinality > TupleRestPosition - 1 || projection.HasAuthoredElementNames) &&
                cardinality >= start &&
                (count == 0 || result.Count < count))
            {
                ManagedValueDisplay rawDisplay = _services.FormatRuntimeValue(
                    value,
                    debuggerDisplayDepth: 0,
                    customTypeInfo);
                ManagedValueReferences rawReferences = _services.RetainValue(
                    value,
                    generation,
                    parentEvaluateName,
                    frameId,
                    ManagedValueView.Raw,
                    customTypeInfo,
                    origin,
                    lifetime);
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

    /// <summary>
    /// Resolves one authored or positional tuple element to its physical runtime field.
    /// </summary>
    /// <param name="value">The dereferenced and unboxed tuple value.</param>
    /// <param name="exactType">The exact compatible tuple type.</param>
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="elementName">The authored or ItemN element name.</param>
    /// <param name="comparison">The source-language name comparison.</param>
    /// <param name="element">Receives the retained physical field value, nested tuple metadata, and origin.</param>
    /// <param name="origin">The exact physical storage of the tuple value.</param>
    /// <returns>True when exactly one logical element matches.</returns>
    internal bool TryGetElementValue(
        nint value,
        nint exactType,
        ManagedTupleCustomTypeInfo? customTypeInfo,
        string elementName,
        StringComparison comparison,
        out (nint Value, ManagedTupleCustomTypeInfo? CustomTypeInfo, ManagedValueOrigin? Origin) element,
        ManagedValueOrigin? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementName);
        if (!_typeShape.TryCreateProjection(
            exactType,
            customTypeInfo,
            out ManagedTupleTypeProjection projection))
        {
            element = default;
            return false;
        }

        for (int index = 0; index < projection.ElementNames.Count; index++)
        {
            if (string.Equals($"Item{index + 1}", elementName, comparison))
            {
                (nint fieldValue, ManagedValueOrigin? fieldOrigin) = GetLogicalElementValue(
                    value, exactType, index, origin);
                element = (
                    fieldValue,
                    projection.ElementCustomTypeInfo[index],
                    fieldOrigin);
                return true;
            }
        }

        int? matchingIndex = null;
        for (int index = 0; index < projection.ElementNames.Count; index++)
        {
            if (!string.Equals(projection.ElementNames[index], elementName, comparison))
            {
                continue;
            }

            if (matchingIndex is not null)
            {
                element = default;
                return false;
            }

            matchingIndex = index;
        }

        if (matchingIndex is null)
        {
            element = default;
            return false;
        }

        (nint matchingValue, ManagedValueOrigin? matchingOrigin) = GetLogicalElementValue(
            value, exactType, matchingIndex.Value, origin);
        element = (
            matchingValue,
            projection.ElementCustomTypeInfo[matchingIndex.Value],
            matchingOrigin);
        return true;
    }

    /// <summary>
    /// Gets authored and positional completion names for one compatible tuple type.
    /// </summary>
    /// <param name="type">The exact compatible tuple type.</param>
    /// <param name="customTypeInfo">The optional tuple-name transforms.</param>
    /// <returns>The ordered distinct logical member names, or an empty list.</returns>
    internal IReadOnlyList<string> GetCompletionNames(
        nint type,
        ManagedTupleCustomTypeInfo? customTypeInfo)
    {
        if (!_typeShape.TryCreateProjection(
            type,
            customTypeInfo,
            out ManagedTupleTypeProjection projection))
        {
            return [];
        }

        List<string> result = new(projection.ElementNames.Count * 2);
        for (int index = 0; index < projection.ElementNames.Count; index++)
        {
            result.Add(projection.ElementNames[index]);
            string positionalName = $"Item{index + 1}";
            if (!string.Equals(
                projection.ElementNames[index],
                positionalName,
                StringComparison.Ordinal))
            {
                result.Add(positionalName);
            }
        }

        return result;
    }

    private void VisitValues<TState>(
        nint value,
        nint exactType,
        ManagedTupleTypeProjection projection,
        Action<nint, int, string?, ManagedTupleCustomTypeInfo?, ManagedValueOrigin?, TState> visitor,
        TState state,
        string? parentEvaluateName = null,
        ManagedValueOrigin? origin = null)
    {
        ManagedTupleTraversalState traversal = new(
            Retain(value),
            Retain(exactType),
            parentEvaluateName,
            origin);
        try
        {
            int remaining = projection.ElementNames.Count;
            int logicalIndex = 0;
            for (int tupleDepth = 0; tupleDepth < MaximumTupleDepth; tupleDepth++)
            {
                int fieldCount = Math.Min(remaining, TupleRestPosition - 1);
                for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                {
                    string physicalName = $"Item{fieldIndex + 1}";
                    (nint fieldValue, ManagedValueOrigin? fieldOrigin) = GetFieldValue(
                        traversal.CurrentValue,
                        traversal.CurrentType,
                        physicalName,
                        traversal.Origin);
                    try
                    {
                        visitor(
                            fieldValue,
                            logicalIndex,
                            ManagedExpressionName.CreateMember(
                                traversal.LayerEvaluateName,
                                physicalName),
                            projection.ElementCustomTypeInfo[logicalIndex],
                            fieldOrigin,
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

                MoveToRest(traversal);
            }

            throw new InvalidOperationException(
                $"The tuple exceeds the supported nesting depth of {MaximumTupleDepth}.");
        }
        finally
        {
            if (traversal.CurrentType != 0)
            {
                _ = ComAbi.Release(traversal.CurrentType);
            }

            if (traversal.CurrentValue != 0)
            {
                _ = ComAbi.Release(traversal.CurrentValue);
            }
        }
    }

    private (nint Value, ManagedValueOrigin? Origin) GetLogicalElementValue(
        nint value,
        nint exactType,
        int logicalIndex,
        ManagedValueOrigin? origin)
    {
        ManagedTupleTraversalState traversal = new(
            Retain(value),
            Retain(exactType),
            layerEvaluateName: null,
            origin);
        try
        {
            int remainingIndex = logicalIndex;
            for (int tupleDepth = 0; tupleDepth < MaximumTupleDepth; tupleDepth++)
            {
                if (remainingIndex < TupleRestPosition - 1)
                {
                    return GetFieldValue(
                        traversal.CurrentValue,
                        traversal.CurrentType,
                        $"Item{remainingIndex + 1}",
                        traversal.Origin);
                }

                remainingIndex -= TupleRestPosition - 1;
                MoveToRest(traversal);
            }

            throw new InvalidOperationException(
                $"The tuple exceeds the supported nesting depth of {MaximumTupleDepth}.");
        }
        finally
        {
            if (traversal.CurrentType != 0)
            {
                _ = ComAbi.Release(traversal.CurrentType);
            }

            if (traversal.CurrentValue != 0)
            {
                _ = ComAbi.Release(traversal.CurrentValue);
            }
        }
    }

    private void MoveToRest(ManagedTupleTraversalState traversal)
    {
        (nint restValue, ManagedValueOrigin? restOrigin) = GetFieldValue(
            traversal.CurrentValue,
            traversal.CurrentType,
            "Rest",
            traversal.Origin);
        nint restType = 0;
        try
        {
            restType = GetExactType(restValue);
            _ = ComAbi.Release(traversal.CurrentValue);
            traversal.CurrentValue = restValue;
            restValue = 0;
            _ = ComAbi.Release(traversal.CurrentType);
            traversal.CurrentType = restType;
            restType = 0;
            traversal.LayerEvaluateName = ManagedExpressionName.CreateMember(
                traversal.LayerEvaluateName,
                "Rest");
            traversal.Origin = restOrigin;
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

    private (nint Value, ManagedValueOrigin? Origin) GetFieldValue(
        nint value,
        nint type,
        string fieldName,
        ManagedValueOrigin? origin)
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
                    uint fieldToken = checked((uint)MetadataTokens.GetToken(fieldHandle));
                    ManagedValueOrigin? fieldOrigin = _services.CreateFieldValueOrigin(
                        origin, runtimeClass, fieldToken);
                    return (
                        GetObjectFieldValue(instance, runtimeClass, fieldToken),
                        fieldOrigin);
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
