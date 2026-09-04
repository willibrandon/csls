using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Expands managed object fields with bounded debugger presentation semantics.
/// </summary>
internal sealed class ManagedObjectExpander
{
    private const int MaximumDebuggerBrowsableNestingDepth = 32;
    private const int MaximumExpandableValueCount = 64 * 1024;
    private const int MaximumTypeHierarchyDepth = 256;
    private readonly IManagedObjectExpansionServices _services;
    private readonly ManagedDebuggerTypeProxyExpander _proxyExpander;
    private readonly ManagedTuplePresenter _tuplePresenter;

    /// <summary>
    /// Creates an object expander over one generation-owned runtime service.
    /// </summary>
    /// <param name="services">The runtime operations used to inspect and retain values.</param>
    /// <param name="tuplePresenter">The owned tuple-specific presentation component.</param>
    internal ManagedObjectExpander(
        IManagedObjectExpansionServices services,
        ManagedTuplePresenter tuplePresenter)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tuplePresenter);
        _services = services;
        _proxyExpander = new ManagedDebuggerTypeProxyExpander(services, this);
        _tuplePresenter = tuplePresenter;
    }

    /// <summary>
    /// Expands one logical page of fields from a stopped managed object.
    /// </summary>
    /// <param name="value">The retained, dereferenced ICorDebugValue pointer.</param>
    /// <param name="parentEvaluateName">The optional source expression for the object.</param>
    /// <param name="frameId">The optional generation-owned frame identifier.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="start">The zero-based first logical child.</param>
    /// <param name="count">The maximum count, or zero for every remaining child.</param>
    /// <param name="view">The presentation view applied to the expansion.</param>
    /// <param name="tupleCustomTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="proxyRawView">The original object exposed after proxy fields.</param>
    /// <param name="proxyStaticView">The optional synthetic static-member container.</param>
    /// <param name="proxyProperties">The evaluated proxy property rows.</param>
    /// <returns>The requested logical field page.</returns>
    internal List<DebugVariableInfo> Expand(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedValueView view,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedDebuggerTypeProxyRawView? proxyRawView,
        ManagedDebuggerTypeProxyStaticView? proxyStaticView,
        IReadOnlyList<ManagedDebuggerTypeProxyPropertyPresentation>? proxyProperties)
    {
        if (proxyRawView is not null)
        {
            return _proxyExpander.Expand(
                value,
                parentEvaluateName,
                frameId,
                generation,
                start,
                count,
                proxyRawView,
                proxyStaticView,
                proxyProperties ?? []);
        }

        if (view != ManagedValueView.Raw &&
            _tuplePresenter.TryExpand(
                value,
                parentEvaluateName,
                frameId,
                generation,
                tupleCustomTypeInfo,
                start,
                count,
                out List<DebugVariableInfo> tupleResult))
        {
            return tupleResult;
        }

        var path = new HashSet<ulong>();
        ulong address = TryGetManagedValueAddress(value);
        if (address != 0)
        {
            _ = path.Add(address);
        }

        var result = new List<DebugVariableInfo>();
        var state = new ManagedObjectExpansionState();
        AppendObjectFields(
            result,
            value,
            parentEvaluateName,
            frameId,
            generation,
            start,
            count,
            state,
            path,
            nestingDepth: 0,
            view,
            includeRawView: true);
        return result;
    }

    /// <summary>
    /// Materializes the proxy's synthetic static-member container.
    /// </summary>
    /// <param name="value">The retained, dereferenced proxy value.</param>
    /// <param name="frame">The optional active frame used for context-local statics.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="properties">The evaluated proxy property presentations.</param>
    /// <returns>The bounded ordered static member rows.</returns>
    internal List<DebugVariableInfo> MaterializeDebuggerTypeProxyStaticMembers(
        nint value,
        nint frame,
        DebugStopGeneration generation,
        IReadOnlyList<ManagedDebuggerTypeProxyPropertyPresentation> properties) =>
        _proxyExpander.MaterializeStaticMembers(value, frame, generation, properties);

    private unsafe void AppendObjectFields(
        List<DebugVariableInfo> result,
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state,
        HashSet<ulong> path,
        int nestingDepth,
        ManagedValueView view,
        bool includeRawView)
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

            for (int depth = 0;
                currentType != 0 && depth < MaximumTypeHierarchyDepth;
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
                    using PEReader peReader = _services.OpenRuntimeModule(module);
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
                        state,
                        path,
                        nestingDepth,
                        view);
                    if (IsVariablePageFull(result, count))
                    {
                        return;
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
                    $"{MaximumTypeHierarchyDepth}.");
            }

            if (includeRawView &&
                view != ManagedValueView.Raw &&
                state.WasTransformed &&
                !IsVariablePageFull(result, count))
            {
                AppendRawView(
                    result,
                    value,
                    parentEvaluateName,
                    frameId,
                    generation,
                    start,
                    count,
                    state);
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
        ManagedObjectExpansionState state,
        HashSet<ulong> path,
        int nestingDepth,
        ManagedValueView view)
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

            ManagedDebuggerBrowsableState declaredBrowsingState =
                ManagedDebuggerBrowsableState.Collapsed;
            if (view != ManagedValueView.Raw)
            {
                _ = ManagedDebuggerAttributeReader.TryGetBrowsableState(
                    metadata,
                    field.GetCustomAttributes(),
                    out declaredBrowsingState);
            }

            ManagedDebuggerBrowsableState browsingState = view == ManagedValueView.Raw
                ? ManagedDebuggerBrowsableState.Collapsed
                : declaredBrowsingState;
            if (browsingState == ManagedDebuggerBrowsableState.Never)
            {
                state.WasTransformed = true;
            }
            else if (browsingState == ManagedDebuggerBrowsableState.RootHidden)
            {
                state.WasTransformed = true;
                string name = metadata.GetString(field.Name);
                string? evaluateName = ManagedExpressionName.CreateMember(
                    parentEvaluateName,
                    name);
                if (!TryAppendRootHiddenField(
                    result,
                    instance,
                    declaringClass,
                    fieldHandle,
                    evaluateName,
                    frameId,
                    generation,
                    start,
                    count,
                    state,
                    path,
                    nestingDepth))
                {
                    AppendField(
                        result,
                        instance,
                        declaringClass,
                        metadata,
                        fieldHandle,
                        field,
                        parentEvaluateName,
                        frameId,
                        generation,
                        start,
                        count,
                        state);
                }
            }
            else
            {
                AppendField(
                    result,
                    instance,
                    declaringClass,
                    metadata,
                    fieldHandle,
                    field,
                    parentEvaluateName,
                    frameId,
                    generation,
                    start,
                    count,
                    state);
            }

            state.PhysicalFieldCount++;
            if (IsVariablePageFull(result, count))
            {
                return;
            }

            if (state.PhysicalFieldCount > MaximumExpandableValueCount)
            {
                throw new InvalidOperationException(
                    $"The object exceeds the field limit of {MaximumExpandableValueCount}.");
            }
        }
    }

    private void AppendField(
        List<DebugVariableInfo> result,
        nint instance,
        nint declaringClass,
        MetadataReader metadata,
        FieldDefinitionHandle fieldHandle,
        FieldDefinition field,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, additionalCount: 1);
        if (state.VisibleIndex >= start && !IsVariablePageFull(result, count))
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

        state.VisibleIndex++;
    }

    private bool TryAppendRootHiddenField(
        List<DebugVariableInfo> result,
        nint instance,
        nint declaringClass,
        FieldDefinitionHandle fieldHandle,
        string? evaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state,
        HashSet<ulong> path,
        int nestingDepth)
    {
        nint fieldValue = 0;
        nint inspectedValue = 0;
        nint array = 0;
        nint objectValue = 0;
        ulong address = 0;
        bool addedToPath = false;
        try
        {
            fieldValue = GetObjectFieldValue(
                instance,
                declaringClass,
                checked((uint)MetadataTokens.GetToken(fieldHandle)));
            if (!_services.TryDereferenceValue(fieldValue, out inspectedValue))
            {
                return false;
            }

            if (ComAbi.TryQueryInterface(
                inspectedValue,
                ICorDebugArrayValueAbi.InterfaceId,
                out array))
            {
                int elementCount = checked((int)GetArrayElementCount(
                    new ICorDebugArrayValueAbi(array)));
                EnsureExpandableValueLimit(state.VisibleIndex, elementCount);
                int localStart = Math.Clamp(start - state.VisibleIndex, 0, elementCount);
                int localCount = count == 0 ? 0 : count - result.Count;
                result.AddRange(_services.ExpandArray(
                    array,
                    evaluateName,
                    frameId,
                    generation,
                    null,
                    localStart,
                    localCount));
                state.VisibleIndex += elementCount;
                return true;
            }

            if (nestingDepth >= MaximumDebuggerBrowsableNestingDepth ||
                !ComAbi.TryQueryInterface(
                    inspectedValue,
                    ICorDebugObjectValueAbi.InterfaceId,
                    out objectValue))
            {
                return false;
            }

            address = TryGetManagedValueAddress(inspectedValue);
            if (address != 0 && !path.Add(address))
            {
                return false;
            }

            addedToPath = address != 0;
            AppendObjectFields(
                result,
                inspectedValue,
                evaluateName,
                frameId,
                generation,
                start,
                count,
                state,
                path,
                nestingDepth + 1,
                ManagedValueView.Default,
                includeRawView: false);
            if (addedToPath)
            {
                _ = path.Remove(address);
                addedToPath = false;
            }

            return true;
        }
        finally
        {
            if (addedToPath)
            {
                _ = path.Remove(address);
            }

            if (objectValue != 0)
            {
                _ = ComAbi.Release(objectValue);
            }

            if (array != 0)
            {
                _ = ComAbi.Release(array);
            }

            if (inspectedValue != 0)
            {
                _ = ComAbi.Release(inspectedValue);
            }

            if (fieldValue != 0)
            {
                _ = ComAbi.Release(fieldValue);
            }
        }
    }

    private void AppendRawView(
        List<DebugVariableInfo> result,
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, additionalCount: 1);
        if (state.VisibleIndex >= start && !IsVariablePageFull(result, count))
        {
            ManagedValueDisplay display = _services.FormatRuntimeValue(
                value,
                debuggerDisplayDepth: 0,
                tupleCustomTypeInfo: null);
            ManagedValueReferences references = _services.RetainValue(
                value,
                generation,
                parentEvaluateName,
                frameId,
                ManagedValueView.Raw,
                tupleCustomTypeInfo: null);
            result.Add(new DebugVariableInfo(
                "Raw View",
                display.Value,
                display.Type,
                references.VariablesReference,
                references.MemoryReference,
                EvaluateName: null,
                DebugVariablePresentationKind.Virtual));
        }

        state.VisibleIndex++;
    }

    private void AppendProxyRawView(
        List<DebugVariableInfo> result,
        ManagedDebuggerTypeProxyRawView rawView,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, additionalCount: 1);
        if (state.VisibleIndex >= start && !IsVariablePageFull(result, count))
        {
            ManagedValueDisplay display = _services.FormatRuntimeValue(
                rawView.Pointer,
                debuggerDisplayDepth: 0,
                tupleCustomTypeInfo: null);
            result.Add(new DebugVariableInfo(
                "Raw View",
                display.Value,
                display.Type,
                rawView.VariablesReference,
                rawView.MemoryReference,
                EvaluateName: null,
                DebugVariablePresentationKind.Virtual));
        }

        state.VisibleIndex++;
    }

    private DebugVariableInfo ReadInstanceField(
        nint instance,
        nint declaringClass,
        MetadataReader metadata,
        FieldDefinitionHandle fieldHandle,
        FieldDefinition field,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation)
    {
        nint fieldValue = GetObjectFieldValue(
            instance,
            declaringClass,
            checked((uint)MetadataTokens.GetToken(fieldHandle)));
        try
        {
            ManagedTupleCustomTypeInfo? tupleCustomTypeInfo =
                ManagedTupleElementNameReader.ReadAttribute(
                    metadata,
                    field.GetCustomAttributes());
            ManagedValueDisplay display = _services.FormatRuntimeValue(
                fieldValue,
                debuggerDisplayDepth: 0,
                tupleCustomTypeInfo);
            ManagedDebuggerDisplayAttribute? memberDisplay =
                ManagedDebuggerAttributeReader.GetMemberDisplay(metadata, field);
            if (memberDisplay is not null)
            {
                display = _services.ApplyMemberDisplay(
                    instance,
                    display,
                    debuggerDisplayDepth: 0,
                    memberDisplay);
            }

            string name = metadata.GetString(field.Name);
            string? evaluateName = ManagedExpressionName.CreateMember(
                parentEvaluateName,
                name);
            ManagedValueReferences references = _services.RetainValue(
                fieldValue,
                generation,
                evaluateName,
                frameId,
                ManagedValueView.Default,
                tupleCustomTypeInfo);
            return new DebugVariableInfo(
                display.Name ?? name,
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

    private static bool IsVariablePageFull(
        List<DebugVariableInfo> result,
        int count) => count > 0 && result.Count >= count;

    private static void EnsureExpandableValueLimit(int visibleIndex, int additionalCount)
    {
        if (additionalCount < 0 ||
            visibleIndex > MaximumExpandableValueCount - additionalCount)
        {
            throw new InvalidOperationException(
                $"The object exceeds the field limit of {MaximumExpandableValueCount}.");
        }
    }

    private static unsafe uint GetArrayElementCount(ICorDebugArrayValueAbi array)
    {
        uint count = 0;
        uint* countAddress = &count;
        CorDebugHResult.ThrowIfFailed(
            array.GetCount((nint)countAddress),
            "ICorDebugArrayValue.GetCount");
        return Volatile.Read(ref *countAddress);
    }

    private static unsafe ulong TryGetManagedValueAddress(nint value)
    {
        ulong address = 0;
        ulong* addressPointer = &address;
        int result = new ICorDebugValueAbi(value).GetAddress((nint)addressPointer);
        return result >= 0 ? Volatile.Read(ref *addressPointer) : 0;
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
        nint result = 0;
        nint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetModule((nint)resultAddress),
            "ICorDebugClass.GetModule");
        return RequirePointer(result, "ICorDebugClass.GetModule");
    }

    private static unsafe uint GetClassToken(nint runtimeClass)
    {
        uint result = 0;
        uint* resultAddress = &result;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetToken((nint)resultAddress),
            "ICorDebugClass.GetToken");
        return Volatile.Read(ref *resultAddress);
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

    private static nint RequirePointer(nint value, string operation) =>
        Volatile.Read(ref value) != 0
            ? value
            : throw new InvalidOperationException($"{operation} returned no value.");

}
