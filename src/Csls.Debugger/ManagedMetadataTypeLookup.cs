using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Finds type definitions and forwarding identities within bounded module metadata.
/// </summary>
internal static class ManagedMetadataTypeLookup
{
    private const int MaximumForwardingDepth = 256;
    private const int MaximumTypeScanCount = 1_000_000;

    /// <summary>
    /// Gets the exact forwarding AssemblyRef token for the selected exported type.
    /// </summary>
    internal static uint GetForwardedAssemblyReference(MetadataReader metadata, string metadataName)
    {
        foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
        {
            if (!string.Equals(GetExportedTypeName(metadata, handle), metadataName, StringComparison.Ordinal))
            {
                continue;
            }

            ExportedType type = metadata.GetExportedType(handle);
            for (int depth = 0; depth < MaximumForwardingDepth; depth++)
            {
                if (type.Implementation.Kind != HandleKind.ExportedType)
                {
                    return type.IsForwarder && type.Implementation.Kind == HandleKind.AssemblyReference
                        ? checked((uint)MetadataTokens.GetToken(type.Implementation))
                        : 0;
                }

                type = metadata.GetExportedType((ExportedTypeHandle)type.Implementation);
            }

            throw new BadImageFormatException("A forwarded type exceeds the supported nesting depth.");
        }

        return 0;
    }

    /// <summary>
    /// Gets the bounded full metadata name of a nested exported type.
    /// </summary>
    internal static string GetExportedTypeName(
        MetadataReader metadata,
        ExportedTypeHandle handle)
    {
        List<string> names = [];
        for (int depth = 0; depth < MaximumForwardingDepth; depth++)
        {
            ExportedType type = metadata.GetExportedType(handle);
            names.Add(metadata.GetString(type.Name));
            if (type.Implementation.Kind != HandleKind.ExportedType)
            {
                names.Reverse();
                string name = string.Join('+', names);
                string typeNamespace = metadata.GetString(type.Namespace);
                return string.IsNullOrEmpty(typeNamespace)
                    ? name
                    : $"{typeNamespace}.{name}";
            }

            handle = (ExportedTypeHandle)type.Implementation;
        }

        throw new BadImageFormatException(
            $"An exported type exceeds {MaximumForwardingDepth} nested levels.");
    }

    /// <summary>
    /// Finds one full type-definition name within the shared metadata inspection budget.
    /// </summary>
    internal static uint? FindDefinition(
        MetadataReader metadata,
        string metadataName,
        string? assemblyName,
        ref int scannedTypes)
    {
        if (assemblyName is not null &&
            (!metadata.IsAssembly || !string.Equals(
                metadata.GetString(metadata.GetAssemblyDefinition().Name),
                assemblyName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            if (++scannedTypes > MaximumTypeScanCount)
            {
                throw new InvalidOperationException(
                    $"Runtime type resolution exceeds {MaximumTypeScanCount} loaded types.");
            }

            if (string.Equals(
                GetMetadataTypeName(metadata, handle),
                metadataName,
                StringComparison.Ordinal))
            {
                return checked((uint)MetadataTokens.GetToken(handle));
            }
        }

        return null;
    }

    private static string GetMetadataTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition initial = metadata.GetTypeDefinition(handle);
        if (initial.GetDeclaringType().IsNil)
        {
            string name = metadata.GetString(initial.Name);
            string typeNamespace = metadata.GetString(initial.Namespace);
            return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
        }
        List<string> names = [];
        for (int depth = 0; depth < MaximumForwardingDepth; depth++)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            names.Add(metadata.GetString(type.Name));
            TypeDefinitionHandle declaringType = type.GetDeclaringType();
            if (declaringType.IsNil)
            {
                names.Reverse();
                string name = string.Join('+', names);
                string typeNamespace = metadata.GetString(type.Namespace);
                return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
            }
            handle = declaringType;
        }
        throw new BadImageFormatException(
            $"A type definition exceeds {MaximumForwardingDepth} nested levels.");
    }
}
