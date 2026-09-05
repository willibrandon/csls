using System.Reflection.Metadata;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded tuple-name transforms from PE metadata and Portable PDBs.
/// </summary>
internal static class ManagedTupleElementNameReader
{
    private const int MaximumTransformBlobBytes = 4 * 1024 * 1024;
    private const int MaximumTransformNameCharacters = 16 * 1024;
    private const int MaximumTransformNameBytes = 16 * 1024;
    private const int MaximumTransformNameCount = 64 * 1024;
    private const string TupleElementNamesAttribute =
        "System.Runtime.CompilerServices.TupleElementNamesAttribute";
    private static readonly Guid s_tupleElementNames =
        new("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710");
    private static readonly Encoding s_strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Reads compiler tuple-name transforms attached to one PE metadata entity.
    /// </summary>
    /// <param name="metadata">The metadata containing the attributed entity.</param>
    /// <param name="attributes">The entity custom attributes.</param>
    /// <returns>Validated tuple metadata, or null when no usable transform exists.</returns>
    internal static ManagedTupleCustomTypeInfo? ReadAttribute(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes)
    {
        try
        {
            foreach (CustomAttribute attribute in attributes
                .Select(metadata.GetCustomAttribute)
                .Where(attribute => string.Equals(
                    ManagedDebuggerAttributeReader.GetAttributeTypeName(metadata, attribute),
                    TupleElementNamesAttribute,
                    StringComparison.Ordinal)))
            {
                return DecodeAttribute(metadata.GetBlobReader(attribute.Value));
            }
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or DecoderFallbackException or OverflowException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads tuple-name transforms across aggregate metadata rows and generation-owned heaps.
    /// </summary>
    internal static ManagedTupleCustomTypeInfo? ReadAttribute(ManagedMetadataImage metadata, EntityHandle entity)
    {
        try
        {
            var provider = new ManagedMetadataTypeSignatureProvider(0, metadata);
            foreach (CustomAttribute attribute in metadata.GetCustomAttributes(entity))
            {
                EntityHandle type = metadata.GetAttributeType(attribute);
                string? name = type.Kind switch
                {
                    HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                        metadata.Baseline, (TypeDefinitionHandle)type, 0x12).MetadataName,
                    HandleKind.TypeReference => provider.GetTypeFromReference(
                        metadata.Baseline, (TypeReferenceHandle)type, 0x12).MetadataName,
                    _ => null
                };
                if (string.Equals(name, TupleElementNamesAttribute, StringComparison.Ordinal))
                {
                    return DecodeAttribute(metadata.GetBlobReader(attribute.Value));
                }
            }
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or DecoderFallbackException or OverflowException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads compiler tuple-name transforms attached to one Portable PDB entity.
    /// </summary>
    /// <param name="metadata">The Portable PDB metadata.</param>
    /// <param name="entity">The local variable or constant entity.</param>
    /// <returns>Validated tuple metadata, or null when no usable transform exists.</returns>
    internal static ManagedTupleCustomTypeInfo? ReadPortablePdb(
        MetadataReader metadata,
        EntityHandle entity)
    {
        try
        {
            foreach (CustomDebugInformation information in metadata
                .GetCustomDebugInformation(entity)
                .Select(metadata.GetCustomDebugInformation)
                .Where(information =>
                    metadata.GetGuid(information.Kind) == s_tupleElementNames))
            {
                return DecodePortablePdb(metadata.GetBlobBytes(information.Value));
            }
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or DecoderFallbackException or OverflowException)
        {
            return null;
        }

        return null;
    }

    private static ManagedTupleCustomTypeInfo? DecodeAttribute(BlobReader value)
    {
        if (value.Length > MaximumTransformBlobBytes ||
            value.RemainingBytes < sizeof(ushort) + sizeof(int) + sizeof(ushort) ||
            value.ReadUInt16() != 1)
        {
            return null;
        }

        int count = value.ReadInt32();
        if (count <= 0 || count > MaximumTransformNameCount)
        {
            return null;
        }

        string?[] names = new string?[count];
        for (int index = 0; index < count; index++)
        {
            string? name = value.ReadSerializedString();
            if (name?.Length > MaximumTransformNameCharacters)
            {
                return null;
            }

            names[index] = NullIfEmpty(name);
        }

        return value.RemainingBytes == sizeof(ushort) && value.ReadUInt16() == 0
            ? CreateIfNamed(names)
            : null;
    }

    /// <summary>
    /// Decodes one bounded Portable PDB tuple-name transform blob.
    /// </summary>
    /// <param name="value">The complete custom-debug-information payload.</param>
    /// <returns>Validated tuple metadata, or null when the payload is unusable.</returns>
    internal static ManagedTupleCustomTypeInfo? DecodePortablePdb(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > MaximumTransformBlobBytes || value[^1] != 0)
        {
            return null;
        }

        List<string?> names = [];
        int start = 0;
        while (start < value.Length)
        {
            if (names.Count == MaximumTransformNameCount)
            {
                return null;
            }

            int end = Array.IndexOf(value, (byte)0, start);
            if (end < 0 || end - start > MaximumTransformNameBytes)
            {
                return null;
            }

            try
            {
                names.Add(end == start
                    ? null
                    : s_strictUtf8.GetString(value, start, end - start));
            }
            catch (DecoderFallbackException)
            {
                return null;
            }

            start = end + 1;
        }

        return CreateIfNamed(names);
    }

    private static ManagedTupleCustomTypeInfo? CreateIfNamed(
        IEnumerable<string?> names)
    {
        string?[] snapshot = [.. names];
        return snapshot.Any(static name => !string.IsNullOrEmpty(name))
            ? new ManagedTupleCustomTypeInfo(snapshot)
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
