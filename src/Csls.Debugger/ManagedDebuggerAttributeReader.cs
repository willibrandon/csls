using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Reads debugger presentation attributes directly from bounded ECMA-335 metadata.
/// </summary>
internal static class ManagedDebuggerAttributeReader
{
    private const string DebuggerBrowsableAttribute =
        "System.Diagnostics.DebuggerBrowsableAttribute";

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
