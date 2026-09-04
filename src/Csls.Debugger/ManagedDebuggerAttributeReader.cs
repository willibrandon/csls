using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Reads debugger presentation attributes directly from bounded ECMA-335 metadata.
/// </summary>
internal static class ManagedDebuggerAttributeReader
{
    private const string DebuggerBrowsableAttribute =
        "System.Diagnostics.DebuggerBrowsableAttribute";
    private const string DebuggerDisplayAttribute =
        "System.Diagnostics.DebuggerDisplayAttribute";

    /// <summary>
    /// Gets the validated debugger browsing policy for one managed field.
    /// </summary>
    /// <param name="metadata">The declaring module metadata.</param>
    /// <param name="field">The field whose debugger metadata is inspected.</param>
    /// <returns>The validated browsing policy, or the normal collapsed policy.</returns>
    internal static ManagedDebuggerBrowsableState GetBrowsableState(
        MetadataReader metadata,
        FieldDefinition field)
    {
        try
        {
            foreach (CustomAttribute attribute in field
                .GetCustomAttributes()
                .Select(handle => metadata.GetCustomAttribute(handle)))
            {
                if (!string.Equals(
                    GetAttributeTypeName(metadata, attribute),
                    DebuggerBrowsableAttribute,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                return ReadBrowsableState(metadata, attribute);
            }
        }
        catch (BadImageFormatException)
        {
            return ManagedDebuggerBrowsableState.Collapsed;
        }

        return ManagedDebuggerBrowsableState.Collapsed;
    }

    /// <summary>
    /// Gets the first debugger-display attribute declared by one runtime type.
    /// </summary>
    /// <param name="metadata">The declaring module metadata.</param>
    /// <param name="type">The runtime type definition.</param>
    /// <returns>The validated display metadata, or null when none is usable.</returns>
    internal static ManagedDebuggerDisplayAttribute? GetDeclaredDisplay(
        MetadataReader metadata,
        TypeDefinition type)
    {
        try
        {
            return FindDisplay(metadata, type.GetCustomAttributes(), targetTypeName: null);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the first assembly-level debugger display targeting one runtime type.
    /// </summary>
    /// <param name="metadata">The declaring assembly metadata.</param>
    /// <param name="targetTypeName">The reflection-style full target type name.</param>
    /// <returns>The validated display metadata, or null when none is applicable.</returns>
    internal static ManagedDebuggerDisplayAttribute? GetAssemblyDisplay(
        MetadataReader metadata,
        string targetTypeName)
    {
        try
        {
            return metadata.IsAssembly
                ? FindDisplay(
                    metadata,
                    metadata.GetAssemblyDefinition().GetCustomAttributes(),
                    targetTypeName)
                : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static ManagedDebuggerBrowsableState ReadBrowsableState(
        MetadataReader metadata,
        CustomAttribute attribute)
    {
        try
        {
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.RemainingBytes != 8 || value.ReadUInt16() != 1)
            {
                return ManagedDebuggerBrowsableState.Collapsed;
            }

            int state = value.ReadInt32();
            if (value.ReadUInt16() != 0 || value.RemainingBytes != 0)
            {
                return ManagedDebuggerBrowsableState.Collapsed;
            }

            return state switch
            {
                0 => ManagedDebuggerBrowsableState.Never,
                3 => ManagedDebuggerBrowsableState.RootHidden,
                _ => ManagedDebuggerBrowsableState.Collapsed
            };
        }
        catch (BadImageFormatException)
        {
            return ManagedDebuggerBrowsableState.Collapsed;
        }
    }

    private static ManagedDebuggerDisplayAttribute? FindDisplay(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes,
        string? targetTypeName)
    {
        foreach (CustomAttribute attribute in attributes.Select(
            handle => metadata.GetCustomAttribute(handle)))
        {
            if (!string.Equals(
                GetAttributeTypeName(metadata, attribute),
                DebuggerDisplayAttribute,
                StringComparison.Ordinal))
            {
                continue;
            }

            ManagedDebuggerDisplayMetadata? display = ReadDisplay(metadata, attribute);
            if (display is null ||
                targetTypeName is not null &&
                !MatchesTargetType(display.TargetTypeName, targetTypeName))
            {
                continue;
            }

            return new ManagedDebuggerDisplayAttribute(
                display.Value,
                display.Name,
                display.Type);
        }

        return null;
    }

    private static ManagedDebuggerDisplayMetadata? ReadDisplay(
        MetadataReader metadata,
        CustomAttribute attribute)
    {
        try
        {
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                return null;
            }

            string displayValue = value.ReadSerializedString() ?? string.Empty;
            string? name = null;
            string? type = null;
            string? targetTypeName = null;
            int namedCount = value.ReadUInt16();
            for (int index = 0; index < namedCount; index++)
            {
                byte kind = value.ReadByte();
                byte valueType = value.ReadByte();
                if (kind is not (0x53 or 0x54) || valueType is not (0x0e or 0x50))
                {
                    return null;
                }

                string? propertyName = value.ReadSerializedString();
                string? propertyValue = value.ReadSerializedString();
                if (propertyName is null)
                {
                    return null;
                }

                if (string.Equals(propertyName, "Name", StringComparison.Ordinal) &&
                    valueType == 0x0e)
                {
                    name = propertyValue;
                }
                else if (string.Equals(propertyName, "Type", StringComparison.Ordinal) &&
                    valueType == 0x0e)
                {
                    type = propertyValue;
                }
                else if ((string.Equals(propertyName, "Target", StringComparison.Ordinal) &&
                        valueType == 0x50) ||
                    (string.Equals(propertyName, "TargetTypeName", StringComparison.Ordinal) &&
                        valueType == 0x0e))
                {
                    targetTypeName = propertyValue;
                }
            }

            return value.RemainingBytes == 0
                ? new ManagedDebuggerDisplayMetadata(
                    displayValue,
                    NullIfEmpty(name),
                    NullIfEmpty(type),
                    NullIfEmpty(targetTypeName))
                : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static bool MatchesTargetType(string? encodedTarget, string targetTypeName)
    {
        if (encodedTarget is null)
        {
            return false;
        }

        int assemblySeparator = encodedTarget.IndexOf(',', StringComparison.Ordinal);
        ReadOnlySpan<char> fullName = assemblySeparator < 0
            ? encodedTarget.AsSpan()
            : encodedTarget.AsSpan(0, assemblySeparator);
        return fullName.Trim().Equals(targetTypeName, StringComparison.Ordinal);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string? GetAttributeTypeName(
        MetadataReader metadata,
        CustomAttribute attribute)
    {
        EntityHandle type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata
                .GetMemberReference((MemberReferenceHandle)attribute.Constructor)
                .Parent,
            HandleKind.MethodDefinition => metadata
                .GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                .GetDeclaringType(),
            _ => default
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => GetTypeName(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)type)),
            HandleKind.TypeDefinition => GetTypeName(
                metadata,
                metadata.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => null
        };
    }

    private static string GetTypeName(MetadataReader metadata, TypeReference type) =>
        JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string GetTypeName(MetadataReader metadata, TypeDefinition type) =>
        JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string JoinTypeName(string namespaceName, string name) =>
        string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";
}
