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
    private const string DebuggerTypeProxyAttribute =
        "System.Diagnostics.DebuggerTypeProxyAttribute";

    /// <summary>
    /// Gets the validated debugger browsing policy for one managed field.
    /// </summary>
    /// <param name="metadata">The declaring module metadata.</param>
    /// <param name="field">The field whose debugger metadata is inspected.</param>
    /// <returns>The validated browsing policy, or the normal collapsed policy.</returns>
    internal static ManagedDebuggerBrowsableState GetBrowsableState(
        MetadataReader metadata,
        FieldDefinition field) => TryGetBrowsableState(
            metadata,
            field.GetCustomAttributes(),
            out ManagedDebuggerBrowsableState state)
        ? state
        : ManagedDebuggerBrowsableState.Collapsed;

    /// <summary>
    /// Tries to read one valid debugger browsing policy from a metadata attribute set.
    /// </summary>
    /// <param name="metadata">The metadata containing the custom attributes.</param>
    /// <param name="attributes">The custom attributes to inspect.</param>
    /// <param name="state">Receives the validated browsing policy.</param>
    /// <returns>True when a valid debugger browsing policy was declared.</returns>
    internal static bool TryGetBrowsableState(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes,
        out ManagedDebuggerBrowsableState state)
    {
        state = ManagedDebuggerBrowsableState.Collapsed;
        try
        {
            foreach (CustomAttribute attribute in attributes.Select(
                handle => metadata.GetCustomAttribute(handle)))
            {
                if (!string.Equals(
                    GetAttributeTypeName(metadata, attribute),
                    DebuggerBrowsableAttribute,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                ManagedDebuggerBrowsableState? candidate = ReadBrowsableState(
                    metadata,
                    attribute);
                if (candidate is ManagedDebuggerBrowsableState validState)
                {
                    state = validState;
                    return true;
                }
            }
        }
        catch (BadImageFormatException)
        {
            return false;
        }

        return false;
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
    /// Gets the first debugger-display attribute declared by one managed field.
    /// </summary>
    /// <param name="metadata">The declaring module metadata.</param>
    /// <param name="field">The field whose debugger display is inspected.</param>
    /// <returns>The validated display metadata, or null when none is usable.</returns>
    internal static ManagedDebuggerDisplayAttribute? GetMemberDisplay(
        MetadataReader metadata,
        FieldDefinition field)
    {
        try
        {
            return FindDisplay(metadata, field.GetCustomAttributes(), targetTypeName: null);
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

    /// <summary>
    /// Gets the first debugger type proxy declared by one runtime type.
    /// </summary>
    /// <param name="metadata">The declaring module metadata.</param>
    /// <param name="type">The runtime type definition.</param>
    /// <returns>The validated proxy metadata, or null when none is usable.</returns>
    internal static ManagedDebuggerTypeProxyAttribute? GetDeclaredTypeProxy(
        MetadataReader metadata,
        TypeDefinition type)
    {
        try
        {
            return FindTypeProxy(metadata, type.GetCustomAttributes(), targetTypeName: null);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the first assembly-level debugger type proxy targeting one runtime type.
    /// </summary>
    /// <param name="metadata">The declaring assembly metadata.</param>
    /// <param name="targetTypeName">The reflection-style full target type name.</param>
    /// <returns>The validated proxy metadata, or null when none is applicable.</returns>
    internal static ManagedDebuggerTypeProxyAttribute? GetAssemblyTypeProxy(
        MetadataReader metadata,
        string targetTypeName)
    {
        try
        {
            return metadata.IsAssembly
                ? FindTypeProxy(
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

    private static ManagedDebuggerBrowsableState? ReadBrowsableState(
        MetadataReader metadata,
        CustomAttribute attribute)
    {
        try
        {
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.RemainingBytes != 8 || value.ReadUInt16() != 1)
            {
                return null;
            }

            int state = value.ReadInt32();
            if (value.ReadUInt16() != 0 || value.RemainingBytes != 0)
            {
                return null;
            }

            return state switch
            {
                0 => ManagedDebuggerBrowsableState.Never,
                2 => ManagedDebuggerBrowsableState.Collapsed,
                3 => ManagedDebuggerBrowsableState.RootHidden,
                _ => null
            };
        }
        catch (BadImageFormatException)
        {
            return null;
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

    private static ManagedDebuggerTypeProxyAttribute? FindTypeProxy(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes,
        string? targetTypeName)
    {
        foreach (CustomAttribute attribute in attributes.Select(
            handle => metadata.GetCustomAttribute(handle)))
        {
            if (!string.Equals(
                GetAttributeTypeName(metadata, attribute),
                DebuggerTypeProxyAttribute,
                StringComparison.Ordinal))
            {
                continue;
            }

            (string? ProxyTypeName, string? TargetTypeName)? proxy =
                ReadTypeProxy(metadata, attribute);
            if (proxy is null ||
                targetTypeName is not null &&
                !MatchesTargetType(proxy.Value.TargetTypeName, targetTypeName))
            {
                continue;
            }

            return new ManagedDebuggerTypeProxyAttribute(proxy.Value.ProxyTypeName!);
        }

        return null;
    }

    private static (string? ProxyTypeName, string? TargetTypeName)? ReadTypeProxy(
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

            string? proxyTypeName = NullIfEmpty(value.ReadSerializedString());
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

                if ((string.Equals(propertyName, "Target", StringComparison.Ordinal) &&
                        valueType == 0x50) ||
                    (string.Equals(
                        propertyName,
                        "TargetTypeName",
                        StringComparison.Ordinal) && valueType == 0x0e))
                {
                    targetTypeName = propertyValue;
                }
            }

            return value.RemainingBytes == 0 && proxyTypeName is not null
                ? (proxyTypeName, NullIfEmpty(targetTypeName))
                : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
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

            string? displayValue = value.ReadSerializedString();
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
                    name,
                    type,
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

    /// <summary>
    /// Gets the metadata full name of one custom attribute type.
    /// </summary>
    /// <param name="metadata">The metadata containing the custom attribute.</param>
    /// <param name="attribute">The custom attribute to identify.</param>
    /// <returns>The full type name, or null for an unsupported constructor shape.</returns>
    internal static string? GetAttributeTypeName(
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
