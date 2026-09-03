using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats managed enum values from runtime storage and ECMA-335 metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe bool TryFormatEnumValue(nint value, nint type, out string display)
    {
        nint runtimeClass = 0;
        nint module = 0;
        nint instance = 0;
        nint storage = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            TypeDefinition definition = metadata.GetTypeDefinition(typeHandle);
            if (!IsSystemEnum(metadata, definition.BaseType))
            {
                display = string.Empty;
                return false;
            }

            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            var constants = new List<(string Name, ulong Bits)>();
            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                string name = metadata.GetString(field.Name);
                if (string.Equals(name, "value__", StringComparison.Ordinal))
                {
                    storage = GetObjectFieldValue(
                        instance,
                        runtimeClass,
                        checked((uint)MetadataTokens.GetToken(fieldHandle)));
                    continue;
                }

                if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) !=
                    (FieldAttributes.Static | FieldAttributes.Literal) ||
                    field.GetDefaultValue().IsNil)
                {
                    continue;
                }

                constants.Add((
                    name,
                    ReadEnumConstant(metadata, field.GetDefaultValue())));
            }

            if (storage == 0)
            {
                throw new InvalidOperationException(
                    "The managed enum does not expose its required value__ field.");
            }

            ulong bits = ReadEnumStorage(storage, out ulong mask, out string numericDisplay);
            for (int index = 0; index < constants.Count; index++)
            {
                if ((constants[index].Bits & mask) == bits)
                {
                    display = constants[index].Name;
                    return true;
                }
            }

            if (!HasFlagsAttribute(metadata, typeHandle) || bits == 0)
            {
                display = numericDisplay;
                return true;
            }

            ulong remaining = bits;
            var names = new List<string>();
            var usedValues = new HashSet<ulong>();
            for (int index = 0; index < constants.Count; index++)
            {
                ulong candidate = constants[index].Bits & mask;
                if (candidate == 0 || !usedValues.Add(candidate) ||
                    (remaining & candidate) != candidate)
                {
                    continue;
                }

                names.Add(constants[index].Name);
                remaining &= ~candidate;
                if (remaining == 0)
                {
                    break;
                }
            }

            if (remaining != 0 || names.Count == 0)
            {
                names.Add(remaining == bits ? numericDisplay : remaining.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            display = string.Join(" | ", names);
            return true;
        }
        finally
        {
            if (storage != 0)
            {
                _ = ComAbi.Release(storage);
            }

            if (instance != 0)
            {
                _ = ComAbi.Release(instance);
            }

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

    private static unsafe ulong ReadEnumStorage(
        nint value,
        out ulong mask,
        out string display)
    {
        uint size = 0;
        uint* sizeAddress = &size;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetSize((nint)sizeAddress),
            "ICorDebugValue.GetSize");
        size = Volatile.Read(ref *sizeAddress);
        if (size is 0 or > 8)
        {
            throw new InvalidOperationException(
                $"The managed enum has an unsupported storage size of {size} bytes.");
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

            mask = size == 8 ? ulong.MaxValue : (1UL << checked((int)size * 8)) - 1;
            display = CorDebugValueFormatter.Format(value).Value;
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes) & mask;
        }
        finally
        {
            if (generic != 0)
            {
                _ = ComAbi.Release(generic);
            }
        }
    }

    private static ulong ReadEnumConstant(MetadataReader metadata, ConstantHandle handle)
    {
        Constant constant = metadata.GetConstant(handle);
        BlobReader reader = metadata.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => reader.ReadBoolean() ? 1UL : 0UL,
            ConstantTypeCode.Char => reader.ReadChar(),
            ConstantTypeCode.SByte => unchecked((ulong)reader.ReadSByte()),
            ConstantTypeCode.Byte => reader.ReadByte(),
            ConstantTypeCode.Int16 => unchecked((ulong)reader.ReadInt16()),
            ConstantTypeCode.UInt16 => reader.ReadUInt16(),
            ConstantTypeCode.Int32 => unchecked((ulong)reader.ReadInt32()),
            ConstantTypeCode.UInt32 => reader.ReadUInt32(),
            ConstantTypeCode.Int64 => unchecked((ulong)reader.ReadInt64()),
            ConstantTypeCode.UInt64 => reader.ReadUInt64(),
            _ => throw new BadImageFormatException(
                $"The enum constant uses unsupported type code {constant.TypeCode}.")
        };
    }

    private static bool IsSystemEnum(MetadataReader metadata, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeReference => IsNamedType(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)handle),
                "System",
                "Enum"),
            HandleKind.TypeDefinition => IsNamedType(
                metadata,
                metadata.GetTypeDefinition((TypeDefinitionHandle)handle),
                "System",
                "Enum"),
            _ => false
        };

    private static bool HasFlagsAttribute(
        MetadataReader metadata,
        TypeDefinitionHandle typeHandle)
    {
        foreach (CustomAttributeHandle attributeHandle in metadata
            .GetTypeDefinition(typeHandle)
            .GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(attributeHandle);
            EntityHandle declaringType = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => metadata
                    .GetMemberReference((MemberReferenceHandle)attribute.Constructor)
                    .Parent,
                HandleKind.MethodDefinition => metadata
                    .GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                    .GetDeclaringType(),
                _ => default
            };
            if (declaringType.Kind == HandleKind.TypeReference && IsNamedType(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)declaringType),
                "System",
                "FlagsAttribute") ||
                declaringType.Kind == HandleKind.TypeDefinition && IsNamedType(
                    metadata,
                    metadata.GetTypeDefinition((TypeDefinitionHandle)declaringType),
                    "System",
                    "FlagsAttribute"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNamedType(
        MetadataReader metadata,
        TypeReference type,
        string @namespace,
        string name) =>
        metadata.StringComparer.Equals(type.Namespace, @namespace) &&
        metadata.StringComparer.Equals(type.Name, name);

    private static bool IsNamedType(
        MetadataReader metadata,
        TypeDefinition type,
        string @namespace,
        string name) =>
        metadata.StringComparer.Equals(type.Namespace, @namespace) &&
        metadata.StringComparer.Equals(type.Name, name);
}
