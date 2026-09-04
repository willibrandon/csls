using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Applies bounded debugger display metadata without executing target code.
/// </summary>
internal sealed class ManagedDebuggerDisplayFormatter
{
    private const int MaximumDisplayDepth = 32;
    private const int MaximumTypeHierarchyDepth = 64;
    private readonly IManagedDebuggerDisplayServices _services;

    /// <summary>
    /// Creates a formatter over one generation-owned runtime service.
    /// </summary>
    /// <param name="services">The runtime operations used to inspect nested values.</param>
    internal ManagedDebuggerDisplayFormatter(IManagedDebuggerDisplayServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>
    /// Tries to apply the first applicable display attribute to one runtime value.
    /// </summary>
    /// <param name="value">The dereferenced and unboxed runtime value.</param>
    /// <param name="exactType">The exact runtime type of the value.</param>
    /// <param name="defaultDisplay">The ordinary exact runtime presentation.</param>
    /// <param name="depth">The current debugger-display recursion depth.</param>
    /// <param name="display">Receives the transformed presentation.</param>
    /// <returns>True when a complete safe value template was rendered.</returns>
    internal bool TryFormat(
        nint value,
        nint exactType,
        ManagedValueDisplay defaultDisplay,
        int depth,
        out ManagedValueDisplay display)
    {
        display = default;
        if (depth >= MaximumDisplayDepth)
        {
            return false;
        }

        nint currentType = exactType;
        _ = ComAbi.AddRef(currentType);
        try
        {
            for (int hierarchyDepth = 0;
                currentType != 0 && hierarchyDepth < MaximumTypeHierarchyDepth;
                hierarchyDepth++)
            {
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    runtimeClass = GetRuntimeTypeClass(currentType);
                    module = GetClassModule(runtimeClass);
                    using PEReader peReader = _services.OpenRuntimeModule(module);
                    MetadataReader metadata = peReader.GetMetadataReader();
                    TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                        checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
                    TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                    ManagedDebuggerDisplayAttribute? attribute =
                        ManagedDebuggerAttributeReader.GetDeclaredDisplay(metadata, type) ??
                        ManagedDebuggerAttributeReader.GetAssemblyDisplay(
                            metadata,
                            GetMetadataReflectionTypeName(metadata, typeHandle));
                    if (attribute is not null)
                    {
                        return TryApply(
                            value,
                            currentType,
                            defaultDisplay,
                            depth,
                            attribute,
                            out display);
                    }

                    unsafe
                    {
                        nint* baseTypeAddress = &baseType;
                        CorDebugHResult.ThrowIfFailed(
                            new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                            "ICorDebugType.GetBase");
                        baseType = Volatile.Read(ref *baseTypeAddress);
                    }
                }
                catch (Exception exception) when (IsRecoverablePresentationFailure(exception))
                {
                    return false;
                }
                finally
                {
                    ReleaseIfPresent(module);
                    ReleaseIfPresent(runtimeClass);
                    ReleaseIfPresent(currentType);
                    currentType = baseType;
                }
            }

            return false;
        }
        finally
        {
            ReleaseIfPresent(currentType);
        }
    }

    private bool TryApply(
        nint value,
        nint attributeTargetType,
        ManagedValueDisplay defaultDisplay,
        int depth,
        ManagedDebuggerDisplayAttribute attribute,
        out ManagedValueDisplay display)
    {
        display = default;
        ManagedValueDisplay? Resolve(string expression) => ResolveExpression(
            value,
            attributeTargetType,
            expression,
            depth);
        string text = defaultDisplay.Value;
        if (attribute.Value is not null &&
            !ManagedDebuggerDisplayTemplate.TryRender(attribute.Value, Resolve, out text))
        {
            return false;
        }

        string? name = null;
        if (attribute.Name is not null &&
            ManagedDebuggerDisplayTemplate.TryRender(
                attribute.Name,
                Resolve,
                out string renderedName))
        {
            name = renderedName;
        }

        string type = defaultDisplay.Type;
        if (attribute.Type is not null &&
            ManagedDebuggerDisplayTemplate.TryRender(attribute.Type, Resolve, out string renderedType))
        {
            type = renderedType;
        }

        display = new ManagedValueDisplay(text, type, name);
        return true;
    }

    private ManagedValueDisplay? ResolveExpression(
        nint value,
        nint attributeTargetType,
        string expression,
        int depth)
    {
        string[] segments = expression.Split('.');
        int firstSegment = segments.Length > 0 &&
            (string.Equals(segments[0], "this", StringComparison.Ordinal) ||
                string.Equals(segments[0], "Me", StringComparison.OrdinalIgnoreCase))
                    ? 1
                    : 0;
        if (firstSegment == segments.Length ||
            segments.Length - firstSegment > MaximumDisplayDepth)
        {
            return null;
        }

        nint currentValue = value;
        bool ownsCurrentValue = false;
        try
        {
            for (int index = firstSegment; index < segments.Length; index++)
            {
                string segment = segments[index].Trim();
                if (!ManagedExpressionName.IsSimpleIdentifier(segment) ||
                    !TryGetFieldValue(
                        currentValue,
                        index == firstSegment ? attributeTargetType : 0,
                        segment,
                        out nint fieldValue))
                {
                    return null;
                }

                if (index == segments.Length - 1)
                {
                    try
                    {
                        return _services.FormatRuntimeValue(fieldValue, depth + 1);
                    }
                    finally
                    {
                        ReleaseIfPresent(fieldValue);
                    }
                }

                nint nextValue;
                try
                {
                    if (!TryDereferenceAndUnboxValue(fieldValue, out nextValue))
                    {
                        return null;
                    }
                }
                finally
                {
                    ReleaseIfPresent(fieldValue);
                }

                if (ownsCurrentValue)
                {
                    ReleaseIfPresent(currentValue);
                }

                currentValue = nextValue;
                ownsCurrentValue = true;
            }

            return null;
        }
        finally
        {
            if (ownsCurrentValue)
            {
                ReleaseIfPresent(currentValue);
            }
        }
    }

    private bool TryGetFieldValue(
        nint value,
        nint startingType,
        string fieldName,
        out nint fieldValue)
    {
        nint instance = 0;
        nint value2 = 0;
        nint exactType = 0;
        fieldValue = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            if (startingType != 0)
            {
                exactType = startingType;
                _ = ComAbi.AddRef(exactType);
            }
            else
            {
                value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
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
            }

            return TryGetFieldValue(
                    instance,
                    exactType,
                    fieldName,
                    StringComparison.Ordinal,
                    out fieldValue) ||
                TryGetFieldValue(
                    instance,
                    exactType,
                    fieldName,
                    StringComparison.OrdinalIgnoreCase,
                    out fieldValue);
        }
        catch (Exception exception) when (IsRecoverablePresentationFailure(exception))
        {
            return false;
        }
        finally
        {
            ReleaseIfPresent(exactType);
            ReleaseIfPresent(value2);
            ReleaseIfPresent(instance);
        }
    }

    private bool TryGetFieldValue(
        nint instance,
        nint exactType,
        string fieldName,
        StringComparison comparison,
        out nint fieldValue)
    {
        nint currentType = exactType;
        _ = ComAbi.AddRef(currentType);
        fieldValue = 0;
        try
        {
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
                    using PEReader peReader = _services.OpenRuntimeModule(module);
                    MetadataReader metadata = peReader.GetMetadataReader();
                    TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                        checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
                    foreach ((FieldDefinitionHandle Handle, FieldDefinition Field) candidate in
                        metadata.GetTypeDefinition(typeHandle).GetFields()
                            .Select(handle => (handle, metadata.GetFieldDefinition(handle)))
                            .Where(candidate =>
                                (candidate.Item2.Attributes & FieldAttributes.Static) == 0 &&
                                string.Equals(
                                    metadata.GetString(candidate.Item2.Name),
                                    fieldName,
                                    comparison)))
                    {
                        fieldValue = GetObjectFieldValue(
                            instance,
                            runtimeClass,
                            checked((uint)MetadataTokens.GetToken(candidate.Handle)));
                        return true;
                    }

                    unsafe
                    {
                        nint* baseTypeAddress = &baseType;
                        CorDebugHResult.ThrowIfFailed(
                            new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                            "ICorDebugType.GetBase");
                        baseType = Volatile.Read(ref *baseTypeAddress);
                    }
                }
                finally
                {
                    ReleaseIfPresent(module);
                    ReleaseIfPresent(runtimeClass);
                    ReleaseIfPresent(currentType);
                    currentType = baseType;
                }
            }

            return false;
        }
        finally
        {
            ReleaseIfPresent(currentType);
        }
    }

    private static unsafe bool TryDereferenceAndUnboxValue(nint value, out nint result)
    {
        if (!TryDereferenceValue(value, out nint dereferenced))
        {
            result = 0;
            return false;
        }

        nint box = 0;
        try
        {
            if (!ComAbi.TryQueryInterface(
                dereferenced,
                ICorDebugBoxValueAbi.InterfaceId,
                out box))
            {
                result = dereferenced;
                dereferenced = 0;
                return true;
            }

            nint unboxed = 0;
            nint* unboxedAddress = &unboxed;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBoxValueAbi(box).GetObject((nint)unboxedAddress),
                "ICorDebugBoxValue.GetObject");
            result = RequirePointer(
                Volatile.Read(ref *unboxedAddress),
                "ICorDebugBoxValue.GetObject");
            return true;
        }
        finally
        {
            ReleaseIfPresent(box);
            ReleaseIfPresent(dereferenced);
        }
    }

    private static unsafe bool TryDereferenceValue(nint value, out nint result)
    {
        if (!ComAbi.TryQueryInterface(
            value,
            ICorDebugReferenceValueAbi.InterfaceId,
            out nint reference))
        {
            _ = ComAbi.AddRef(value);
            result = value;
            return true;
        }

        try
        {
            int isNull = 0;
            int* isNullAddress = &isNull;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(reference).IsNull((nint)isNullAddress),
                "ICorDebugReferenceValue.IsNull");
            if (Volatile.Read(ref *isNullAddress) != 0)
            {
                result = 0;
                return false;
            }

            nint dereferenced = 0;
            nint* resultAddress = &dereferenced;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(reference).Dereference((nint)resultAddress),
                "ICorDebugReferenceValue.Dereference");
            result = RequirePointer(
                Volatile.Read(ref *resultAddress),
                "ICorDebugReferenceValue.Dereference");
            return true;
        }
        finally
        {
            ReleaseIfPresent(reference);
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
        return RequirePointer(Volatile.Read(ref *moduleAddress), "ICorDebugClass.GetModule");
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

    private static string GetMetadataReflectionTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition definition = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetMetadataReflectionTypeName(metadata, declaringType)}+{name}";
        }

        string @namespace = metadata.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static nint RequirePointer(nint value, string operation) =>
        Volatile.Read(ref value) != 0
            ? value
            : throw new InvalidOperationException($"{operation} returned no value.");

    private static bool IsRecoverablePresentationFailure(Exception exception) =>
        exception is ArgumentException or BadImageFormatException or IOException or
            InvalidOperationException or NotSupportedException or UnauthorizedAccessException;

    private static void ReleaseIfPresent(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
