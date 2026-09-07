using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats managed values using exact CoreCLR types and loaded module metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedRuntimeTypeFormatter? _runtimeTypes;

    private ManagedRuntimeTypeFormatter RuntimeTypes => _runtimeTypes ??= new ManagedRuntimeTypeFormatter(OpenRuntimeModule);

    private ManagedValueDisplay FormatRuntimeValue(nint value) =>
        FormatRuntimeValue(
            value,
            debuggerDisplayDepth: 0,
            tupleCustomTypeInfo: null);

    private ManagedValueDisplay FormatRuntimeValue(
        nint value,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo) => FormatRuntimeValue(
            value,
            debuggerDisplayDepth: 0,
            tupleCustomTypeInfo);

    private ManagedValueDisplay FormatRuntimeValue(nint value, int debuggerDisplayDepth) =>
        FormatRuntimeValue(value, debuggerDisplayDepth, tupleCustomTypeInfo: null);

    private ManagedValueDisplay FormatRuntimeValue(
        nint value,
        int debuggerDisplayDepth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo) => FormatRuntimeValuePair(
            value, debuggerDisplayDepth, tupleCustomTypeInfo).Presentation;

    private (ManagedValueDisplay Runtime, ManagedValueDisplay Presentation) FormatRuntimeValuePair(
        nint value,
        int debuggerDisplayDepth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
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

            string type = FormatRuntimeType(
                exactType,
                depth: 0,
                tupleCustomTypeInfo,
                out uint elementType,
                out uint? intrinsicElementType);
            ManagedValueDisplay ordinary;
            if (elementType == 0x11 &&
                hasInspectedValue &&
                intrinsicElementType is uint primitiveElementType &&
                CorDebugValueFormatter.TryFormatPrimitiveValueClass(
                    inspectedValue,
                    primitiveElementType,
                    out ManagedValueDisplay primitiveDisplay))
            {
                ordinary = primitiveDisplay;
            }
            else if (elementType == 0x11 &&
                hasInspectedValue &&
                TryFormatEnumValue(inspectedValue, exactType, out string enumDisplay))
            {
                ordinary = new ManagedValueDisplay(enumDisplay, type);
            }
            else if (elementType == 0x11 &&
                hasInspectedValue &&
                string.Equals(type, "decimal", StringComparison.Ordinal) &&
                IsCoreLibraryDefinition(exactType))
            {
                ordinary = new ManagedValueDisplay(
                    FormatDecimalValue(inspectedValue, exactType),
                    type);
            }
            else if (elementType == 0x11 &&
                hasInspectedValue &&
                TryFormatKnownFrameworkValue(
                    inspectedValue,
                    exactType,
                    type,
                    out string frameworkDisplay))
            {
                ordinary = new ManagedValueDisplay(frameworkDisplay, type);
            }
            else if (elementType == 0x11 && IsNullableType(exactType) && hasInspectedValue)
            {
                ordinary = new ManagedValueDisplay(
                    FormatNullableValue(inspectedValue, exactType),
                    type);
            }
            else if (elementType == 0x11 &&
                hasInspectedValue &&
                _tuplePresenter.TryFormatValue(
                    inspectedValue,
                    exactType,
                    debuggerDisplayDepth,
                    tupleCustomTypeInfo,
                    out string tupleDisplay))
            {
                ordinary = new ManagedValueDisplay(tupleDisplay, type);
            }
            else
            {
                string display = elementType switch
                {
                    0x11 => $"{{{type}}}",
                    0x12 when hasInspectedValue => $"{{{type}}}",
                    0x14 or 0x1d when hasInspectedValue => FormatArrayValue(inspectedValue, type),
                    _ => immediate.Value
                };
                ordinary = new ManagedValueDisplay(display, type);
            }

            ManagedValueDisplay presentation = elementType is 0x11 or 0x12 &&
                hasInspectedValue &&
                _debuggerDisplayFormatter.TryFormat(
                    inspectedValue,
                    exactType,
                    ordinary,
                    debuggerDisplayDepth,
                    out ManagedValueDisplay debuggerDisplay)
                    ? debuggerDisplay
                    : ordinary;
            return (ordinary, presentation);
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

    private string FormatRuntimeType(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        out uint elementType) =>
        FormatRuntimeType(type, depth, tupleCustomTypeInfo, out elementType, out _);

    private string FormatRuntimeType(
        nint type,
        int depth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        out uint elementType,
        out uint? intrinsicElementType) =>
        RuntimeTypes.Format(type, depth, tupleCustomTypeInfo, out elementType, out intrinsicElementType);

    private static string? FormatPrimitiveRuntimeType(uint elementType) =>
        ManagedRuntimeTypeFormatter.FormatPrimitiveRuntimeType(elementType);

    private bool IsCoreLibraryDefinition(nint type) => RuntimeTypes.IsCoreLibraryDefinition(type);

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

    private bool IsNullableType(nint type) => ManagedNullableTypeIdentity.IsNullableType(type, OpenRuntimeModule);

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

    private static unsafe ulong ReadIntegralValueBits(nint value, out uint size)
    {
        uint runtimeSize = 0;
        uint* sizeAddress = &runtimeSize;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetSize((nint)sizeAddress),
            "ICorDebugValue.GetSize");
        size = Volatile.Read(ref *sizeAddress);
        if (size is 0 or > 8)
        {
            throw new InvalidOperationException(
                $"The managed integral value has an unsupported size of {size} bytes.");
        }

        nint generic = 0;
        try
        {
            generic = ComAbi.QueryInterface(value, ICorDebugGenericValueAbi.InterfaceId);
            Span<byte> bytes = stackalloc byte[8];
            fixed (byte* bytesAddress = bytes)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugGenericValueAbi(generic).GetValue((nint)bytesAddress),
                    "ICorDebugGenericValue.GetValue");
            }

            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        finally
        {
            if (generic != 0)
            {
                _ = ComAbi.Release(generic);
            }
        }
    }
}
