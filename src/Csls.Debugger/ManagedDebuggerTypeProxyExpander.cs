using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Expands ordered debugger proxy fields, evaluated properties, and the original raw value.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyExpander
{
    private const int MaximumExpandableValueCount = 64 * 1024;
    private const int MaximumTypeHierarchyDepth = 256;
    private readonly ManagedObjectExpander _objectExpander;
    private readonly IManagedObjectExpansionServices _services;

    /// <summary>
    /// Creates a proxy expander over one generation-owned runtime service.
    /// </summary>
    /// <param name="services">The runtime operations used to inspect and retain values.</param>
    /// <param name="objectExpander">The ordinary object expander used by root-hidden fields.</param>
    internal ManagedDebuggerTypeProxyExpander(
        IManagedObjectExpansionServices services,
        ManagedObjectExpander objectExpander)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(objectExpander);
        _services = services;
        _objectExpander = objectExpander;
    }

    /// <summary>
    /// Expands one logical page of a constructed debugger proxy.
    /// </summary>
    /// <param name="value">The retained, dereferenced proxy value.</param>
    /// <param name="parentEvaluateName">The optional proxy source expression.</param>
    /// <param name="frameId">The optional generation-owned frame identifier.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="start">The zero-based first logical child.</param>
    /// <param name="count">The maximum count, or zero for every remaining child.</param>
    /// <param name="rawView">The original object exposed after proxy members.</param>
    /// <param name="properties">The evaluated property presentations.</param>
    /// <returns>The requested ordered proxy-member page.</returns>
    internal List<DebugVariableInfo> Expand(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedDebuggerTypeProxyRawView rawView,
        IReadOnlyList<ManagedDebuggerTypeProxyPropertyPresentation> properties)
    {
        List<ManagedDebuggerTypeProxyFieldBinding> fields = ResolveFields(value);
        nint instance = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            var members = new List<(
                string Name,
                int InheritanceLevel,
                ManagedDebuggerTypeProxyFieldBinding? Field,
                ManagedDebuggerTypeProxyPropertyPresentation? Property)>(
                    fields.Count + properties.Count);
            members.AddRange(fields.Select(static field => (
                field.Name,
                field.InheritanceLevel,
                (ManagedDebuggerTypeProxyFieldBinding?)field,
                (ManagedDebuggerTypeProxyPropertyPresentation?)null)));
            members.AddRange(properties.Select(static property => (
                property.Name,
                InheritanceLevel: 0,
                (ManagedDebuggerTypeProxyFieldBinding?)null,
                (ManagedDebuggerTypeProxyPropertyPresentation?)property)));
            members.Sort(static (left, right) =>
            {
                int nameComparison = string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.Ordinal);
                return nameComparison != 0
                    ? nameComparison
                    : right.InheritanceLevel.CompareTo(left.InheritanceLevel);
            });

            var result = new List<DebugVariableInfo>();
            var state = new ManagedObjectExpansionState();
            foreach ((
                _,
                _,
                ManagedDebuggerTypeProxyFieldBinding? field,
                ManagedDebuggerTypeProxyPropertyPresentation? property) in members)
            {
                if (property is not null)
                {
                    AppendProperty(result, property, start, count, state);
                }
                else if (field is not null &&
                    field.BrowsingState == ManagedDebuggerBrowsableState.RootHidden)
                {
                    if (!TryAppendRootHiddenField(
                        result,
                        instance,
                        field,
                        generation,
                        start,
                        count,
                        state))
                    {
                        AppendField(
                            result,
                            instance,
                            field,
                            parentEvaluateName,
                            frameId,
                            generation,
                            start,
                            count,
                            state);
                    }
                }
                else if (field is not null)
                {
                    AppendField(
                        result,
                        instance,
                        field,
                        parentEvaluateName,
                        frameId,
                        generation,
                        start,
                        count,
                        state);
                }

                if (IsPageFull(result, count))
                {
                    return result;
                }
            }

            AppendRawView(result, rawView, start, count, state);
            return result;
        }
        finally
        {
            if (instance != 0)
            {
                _ = ComAbi.Release(instance);
            }

            ReleaseFields(fields);
        }
    }

    private static void AppendProperty(
        List<DebugVariableInfo> result,
        ManagedDebuggerTypeProxyPropertyPresentation property,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, property.Variables.Count);
        foreach (DebugVariableInfo variable in property.Variables)
        {
            if (state.VisibleIndex >= start && !IsPageFull(result, count))
            {
                result.Add(variable);
            }

            state.VisibleIndex++;
            if (IsPageFull(result, count))
            {
                return;
            }
        }
    }

    private void AppendField(
        List<DebugVariableInfo> result,
        nint instance,
        ManagedDebuggerTypeProxyFieldBinding field,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, additionalCount: 1);
        if (state.VisibleIndex >= start && !IsPageFull(result, count))
        {
            nint fieldValue = GetObjectFieldValue(
                instance,
                field.DeclaringClass,
                field.FieldToken);
            try
            {
                ManagedValueDisplay display = _services.FormatRuntimeValue(
                    fieldValue,
                    debuggerDisplayDepth: 0,
                    field.TupleCustomTypeInfo);
                if (field.MemberDisplay is ManagedDebuggerDisplayAttribute memberDisplay)
                {
                    display = _services.ApplyMemberDisplay(
                        instance,
                        display,
                        debuggerDisplayDepth: 0,
                        memberDisplay);
                }

                string? evaluateName = ManagedExpressionName.CreateMember(
                    parentEvaluateName,
                    field.Name);
                ManagedValueReferences references = _services.RetainValue(
                    fieldValue,
                    generation,
                    evaluateName,
                    frameId,
                    ManagedValueView.Default,
                    field.TupleCustomTypeInfo);
                result.Add(new DebugVariableInfo(
                    display.Name ?? field.Name,
                    display.Value,
                    display.Type,
                    references.VariablesReference,
                    references.MemoryReference,
                    evaluateName));
            }
            finally
            {
                _ = ComAbi.Release(fieldValue);
            }
        }

        state.VisibleIndex++;
    }

    private bool TryAppendRootHiddenField(
        List<DebugVariableInfo> result,
        nint instance,
        ManagedDebuggerTypeProxyFieldBinding field,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        nint fieldValue = 0;
        nint inspectedValue = 0;
        nint array = 0;
        nint objectValue = 0;
        try
        {
            fieldValue = GetObjectFieldValue(
                instance,
                field.DeclaringClass,
                field.FieldToken);
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
                    parentEvaluateName: null,
                    frameId: null,
                    generation,
                    tupleCustomTypeInfo: null,
                    localStart,
                    localCount));
                state.VisibleIndex += elementCount;
                return true;
            }

            if (!ComAbi.TryQueryInterface(
                inspectedValue,
                ICorDebugObjectValueAbi.InterfaceId,
                out objectValue))
            {
                return false;
            }

            List<DebugVariableInfo> children = _objectExpander.Expand(
                inspectedValue,
                parentEvaluateName: null,
                frameId: null,
                generation,
                start: 0,
                count: checked(
                    MaximumExpandableValueCount - state.VisibleIndex + 1),
                ManagedValueView.Default,
                tupleCustomTypeInfo: null,
                proxyRawView: null,
                proxyProperties: null);
            EnsureExpandableValueLimit(state.VisibleIndex, children.Count);
            int first = Math.Clamp(start - state.VisibleIndex, 0, children.Count);
            int take = count == 0
                ? children.Count - first
                : Math.Min(count - result.Count, children.Count - first);
            result.AddRange(children.GetRange(first, take));
            state.VisibleIndex += children.Count;
            return true;
        }
        finally
        {
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
        ManagedDebuggerTypeProxyRawView rawView,
        int start,
        int count,
        ManagedObjectExpansionState state)
    {
        EnsureExpandableValueLimit(state.VisibleIndex, additionalCount: 1);
        if (state.VisibleIndex >= start && !IsPageFull(result, count))
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

    private unsafe List<ManagedDebuggerTypeProxyFieldBinding> ResolveFields(nint value)
    {
        nint value2 = 0;
        nint currentType = 0;
        List<ManagedDebuggerTypeProxyFieldBinding> result = [];
        try
        {
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
                    MetadataReader metadata = peReader.GetMetadataReader();
                    TypeDefinition type = metadata.GetTypeDefinition(
                        MetadataTokens.TypeDefinitionHandle(
                            checked((int)(typeToken & 0x00FFFFFF))));
                    foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                    {
                        AddField(result, metadata, fieldHandle, depth, runtimeClass);
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
                    $"The proxy type hierarchy exceeds the supported depth of " +
                    $"{MaximumTypeHierarchyDepth}.");
            }

            return result;
        }
        catch
        {
            ReleaseFields(result);
            throw;
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
        }
    }

    private static void AddField(
        List<ManagedDebuggerTypeProxyFieldBinding> result,
        MetadataReader metadata,
        FieldDefinitionHandle fieldHandle,
        int inheritanceLevel,
        nint runtimeClass)
    {
        FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
        if ((field.Attributes & FieldAttributes.Static) != 0)
        {
            return;
        }

        bool hasBrowsingState = ManagedDebuggerAttributeReader.TryGetBrowsableState(
            metadata,
            field.GetCustomAttributes(),
            out ManagedDebuggerBrowsableState browsingState);
        if ((!IsVisibleField(field.Attributes) && !hasBrowsingState) ||
            browsingState == ManagedDebuggerBrowsableState.Never)
        {
            return;
        }

        if (result.Count >= MaximumExpandableValueCount)
        {
            throw new InvalidOperationException(
                $"The debugger proxy exceeds the field limit of " +
                $"{MaximumExpandableValueCount}.");
        }

        _ = ComAbi.AddRef(runtimeClass);
        result.Add(new ManagedDebuggerTypeProxyFieldBinding(
            metadata.GetString(field.Name),
            checked((uint)MetadataTokens.GetToken(fieldHandle)),
            browsingState,
            ManagedTupleElementNameReader.ReadAttribute(
                metadata,
                field.GetCustomAttributes()),
            ManagedDebuggerAttributeReader.GetMemberDisplay(metadata, field),
            inheritanceLevel,
            runtimeClass));
    }

    private static bool IsVisibleField(FieldAttributes attributes)
    {
        FieldAttributes accessibility = attributes & FieldAttributes.FieldAccessMask;
        return accessibility is FieldAttributes.Public or
            FieldAttributes.Family or
            FieldAttributes.FamORAssem;
    }

    private static bool IsPageFull(List<DebugVariableInfo> result, int count) =>
        count > 0 && result.Count >= count;

    private static void EnsureExpandableValueLimit(int visibleIndex, int additionalCount)
    {
        if (additionalCount < 0 ||
            visibleIndex > MaximumExpandableValueCount - additionalCount)
        {
            throw new InvalidOperationException(
                $"The debugger proxy exceeds the child limit of " +
                $"{MaximumExpandableValueCount}.");
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

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer
        : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void ReleaseFields(
        IEnumerable<ManagedDebuggerTypeProxyFieldBinding> fields)
    {
        foreach (ManagedDebuggerTypeProxyFieldBinding field in fields)
        {
            field.Release();
        }
    }
}
