using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Reads optional value-type defaults encoded as framework custom attributes.
/// </summary>
internal static class ManagedFunctionStructuredDefaults
{
    /// <summary>
    /// Reads a decimal or DateTime default without loading code into the target.
    /// </summary>
    internal static ManagedExpressionValue? TryCreate(
        ManagedMetadataImage metadata,
        ParameterHandle handle,
        ManagedBoundType parameter,
        ManagedBoundTypeSystem types,
        nint module,
        nint thread)
    {
        string? attributeName = types.IsCoreType(parameter, "System.Decimal", thread)
            ? "System.Runtime.CompilerServices.DecimalConstantAttribute"
            : types.IsCoreType(parameter, "System.DateTime", thread)
                ? "System.Runtime.CompilerServices.DateTimeConstantAttribute"
                : null;
        if (attributeName is null)
        {
            return null;
        }

        var provider = new ManagedMetadataTypeSignatureProvider(module, metadata);
        foreach (CustomAttribute attribute in metadata.GetCustomAttributes(handle))
        {
            EntityHandle typeHandle = metadata.GetAttributeType(attribute);
            ManagedMetadataTypeSignature? signature = typeHandle.Kind switch
            {
                HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                    metadata.Baseline, (TypeDefinitionHandle)typeHandle, 0x12),
                HandleKind.TypeReference => provider.GetTypeFromReference(
                    metadata.Baseline, (TypeReferenceHandle)typeHandle, 0x12),
                _ => null
            };
            if (signature is null ||
                !string.Equals(signature.MetadataName, attributeName, StringComparison.Ordinal))
            {
                continue;
            }

            ManagedBoundType attributeType = types.Bind(signature, [], [], thread);
            if (!types.IsCoreType(attributeType, attributeName, thread))
            {
                continue;
            }

            BlobReader blob = metadata.GetBlobReader(attribute.Value);
            object value = attributeName.EndsWith("DecimalConstantAttribute", StringComparison.Ordinal)
                ? ReadDecimal(ref blob)
                : ReadDateTime(ref blob);
            string displayType = value is decimal ? "decimal" : "System.DateTime";
            return ManagedExpressionValueFactory.FromScalar(value, displayType) with
            {
                DeclaredType = parameter
            };
        }

        return null;
    }

    private static decimal ReadDecimal(ref BlobReader blob)
    {
        if (blob.RemainingBytes != 18 || blob.ReadUInt16() != 1)
        {
            throw new BadImageFormatException("A decimal default has invalid attribute data.");
        }

        byte scale = blob.ReadByte();
        byte sign = blob.ReadByte();
        int high = blob.ReadInt32();
        int middle = blob.ReadInt32();
        int low = blob.ReadInt32();
        if (scale > 28 || blob.ReadUInt16() != 0)
        {
            throw new BadImageFormatException("A decimal default has invalid attribute data.");
        }

        return new decimal(low, middle, high, sign != 0, scale);
    }

    private static DateTime ReadDateTime(ref BlobReader blob)
    {
        if (blob.RemainingBytes != 12 || blob.ReadUInt16() != 1)
        {
            throw new BadImageFormatException("A DateTime default has invalid attribute data.");
        }

        long ticks = blob.ReadInt64();
        if (blob.ReadUInt16() != 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new BadImageFormatException("A DateTime default has invalid attribute data.");
        }

        return new DateTime(ticks);
    }
}
