using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves bounded source type names to unique loaded metadata definitions without target execution.
/// </summary>
internal sealed class ManagedLoadedTypeNameResolver
{
    private const int MaximumTypeScanCount = 1_000_000;
    private readonly SourceBreakpointManager _modules;

    /// <summary>
    /// Creates a name resolver over the session's loaded module catalog.
    /// </summary>
    internal ManagedLoadedTypeNameResolver(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// Resolves one source-language type name or rejects missing and ambiguous definitions.
    /// </summary>
    internal (CorDebugLoadedModule Module, uint TypeToken) Resolve(
        string typeName,
        DebugExpressionLanguage language,
        string operation)
    {
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        bool simpleName = !typeName.Contains('.', StringComparison.Ordinal) &&
            !typeName.Contains('+', StringComparison.Ordinal);
        var matches = new List<(CorDebugLoadedModule Module, uint TypeToken)>();
        int scannedTypeCount = 0;
        foreach (CorDebugLoadedModule module in _modules.GetRuntimeModules())
        {
            scannedTypeCount = AddTypeMatches(
                module,
                typeName,
                simpleName,
                comparison,
                matches,
                scannedTypeCount);
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No loaded runtime type named '{typeName}' is available for {operation}.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Runtime type name '{typeName}' is ambiguous across loaded modules for " +
                $"{operation}." + (simpleName ? " Use its fully qualified metadata name." : string.Empty));
        }

        return matches[0];
    }

    private static int AddTypeMatches(
        CorDebugLoadedModule module,
        string typeName,
        bool simpleName,
        StringComparison comparison,
        List<(CorDebugLoadedModule Module, uint TypeToken)> matches,
        int scannedTypeCount)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return scannedTypeCount;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            if (++scannedTypeCount > MaximumTypeScanCount)
            {
                throw new InvalidOperationException(
                    $"Runtime type-name resolution exceeds the loaded-type scan limit of {MaximumTypeScanCount}.");
            }

            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string candidateName = simpleName
                ? metadata.GetString(type.Name)
                : GetTypeName(metadata, typeHandle);
            if (string.Equals(candidateName, typeName, comparison) ||
                !simpleName && string.Equals(
                    candidateName.Replace('+', '.'),
                    typeName.Replace('+', '.'),
                    comparison))
            {
                matches.Add((
                    module,
                    checked((uint)MetadataTokens.GetToken(typeHandle))));
            }
        }

        return scannedTypeCount;
    }

    private static string GetTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeName(metadata, declaringType)}+{name}";
        }

        string @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }
}
