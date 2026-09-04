using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves metadata types through exact runtime modules and assembly bindings.
/// </summary>
internal sealed class ManagedRuntimeTypeCatalog
{
    private const int MaximumForwardingDepth = 256;
    private const int MaximumModuleCount = 4096;
    private const int MaximumTypeScanCount = 1_000_000;
    private readonly SourceBreakpointManager _modules;

    /// <summary>
    /// Creates a runtime type catalog over the loaded module set.
    /// </summary>
    /// <param name="modules">The loaded runtime-module catalog.</param>
    internal ManagedRuntimeTypeCatalog(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// Resolves a signature through its originating module and runtime assembly reference.
    /// </summary>
    /// <param name="signature">The decoded signature preserving its runtime scope.</param>
    /// <param name="resolvedModule">Receives the borrowed defining runtime module.</param>
    /// <param name="resolvedToken">Receives the exact type-definition token.</param>
    /// <returns>True when the defining runtime type was found.</returns>
    internal bool TryResolveSignature(
        ManagedMetadataTypeSignature signature,
        out CorDebugLoadedModule? resolvedModule,
        out uint resolvedToken)
    {
        resolvedModule = null;
        resolvedToken = 0;
        if (signature.MetadataName is not string metadataName)
        {
            return false;
        }

        if (signature.SourceModule == 0)
        {
            return TryFindLoadedType(
                metadataName, signature.AssemblyName, out resolvedModule, out resolvedToken);
        }

        if (signature.DefinitionToken != 0)
        {
            resolvedModule = _modules.FindModule(signature.SourceModule);
            resolvedToken = signature.DefinitionToken;
            return resolvedModule is not null;
        }

        nint assembly = GetReferencedAssembly(signature.SourceModule, signature.AssemblyReferenceToken);
        try
        {
            int scannedTypes = 0;
            if (FindTypeInAssembly(assembly, metadataName, [], 0, ref scannedTypes) is not { } match)
            {
                return false;
            }

            resolvedModule = match.Module;
            resolvedToken = match.Token;
            return true;
        }
        finally
        {
            ReleasePointer(assembly);
        }
    }

    private (CorDebugLoadedModule Module, uint Token)? FindTypeInAssembly(
        nint assembly,
        string metadataName,
        HashSet<nint> visited,
        int depth,
        ref int scannedTypes)
    {
        if (depth >= MaximumForwardingDepth)
        {
            throw new BadImageFormatException("A runtime type exceeds the supported forwarding depth.");
        }

        nint identity = ComAbi.GetIdentity(assembly);
        try
        {
            if (!visited.Add(identity))
            {
                return null;
            }
        }
        finally
        {
            ReleasePointer(identity);
        }

        List<CorDebugLoadedModule> modules = GetAssemblyModules(assembly);
        (CorDebugLoadedModule Module, uint Token)? result = null;
        foreach (CorDebugLoadedModule module in modules)
        {
            uint? match = TryFindTypeInModule(module, metadataName, null, ref scannedTypes);
            if (match is not null)
            {
                if (result is not null)
                {
                    return null;
                }

                result = (module, match.Value);
            }
        }

        if (result is not null)
        {
            return result;
        }

        foreach (CorDebugLoadedModule module in modules)
        {
            uint assemblyReference = GetForwardedAssemblyReference(module, metadataName);
            if (assemblyReference == 0)
            {
                continue;
            }

            nint forwardedAssembly = GetReferencedAssembly(module.Pointer, assemblyReference);
            try
            {
                if (FindTypeInAssembly(
                    forwardedAssembly, metadataName, visited, depth + 1, ref scannedTypes) is { } match)
                {
                    return match;
                }
            }
            finally
            {
                ReleasePointer(forwardedAssembly);
            }
        }

        return null;
    }

    private unsafe List<CorDebugLoadedModule> GetAssemblyModules(nint assembly)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugAssemblyAbi(assembly).EnumerateModules((nint)enumeratorAddress),
                "ICorDebugAssembly.EnumerateModules");
            enumerator = RequirePointer(
                Volatile.Read(ref *enumeratorAddress), "ICorDebugAssembly.EnumerateModules");
            List<CorDebugLoadedModule> result = [];
            var values = new ICorDebugModuleEnumAbi(enumerator);
            for (int index = 0; index <= MaximumModuleCount; index++)
            {
                nint module = 0;
                try
                {
                    uint fetched = 0;
                    nint* moduleAddress = &module;
                    uint* fetchedAddress = &fetched;
                    CorDebugHResult.ThrowIfFailed(
                        values.Next(1, (nint)moduleAddress, (nint)fetchedAddress),
                        "ICorDebugModuleEnum.Next");
                    module = Volatile.Read(ref *moduleAddress);
                    if (Volatile.Read(ref *fetchedAddress) == 0)
                    {
                        return result;
                    }

                    if (index == MaximumModuleCount)
                    {
                        break;
                    }

                    CorDebugLoadedModule? loaded = _modules.FindModule(
                        RequirePointer(module, "ICorDebugModuleEnum.Next"));
                    if (loaded is not null)
                    {
                        result.Add(loaded);
                    }
                }
                finally
                {
                    ReleasePointer(module);
                }
            }

            throw new InvalidOperationException($"A runtime assembly exceeds {MaximumModuleCount} modules.");
        }
        finally
        {
            ReleasePointer(enumerator);
        }
    }

    private static unsafe nint GetReferencedAssembly(nint module, uint referenceToken)
    {
        nint assembly = 0;
        nint module2 = 0;
        try
        {
            nint* assemblyAddress = &assembly;
            if (referenceToken == 0)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugModuleAbi(module).GetAssembly((nint)assemblyAddress),
                    "ICorDebugModule.GetAssembly");
            }
            else
            {
                module2 = ComAbi.QueryInterface(module, ICorDebugModule2Abi.InterfaceId);
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugModule2Abi(module2).ResolveAssembly(referenceToken, (nint)assemblyAddress),
                    "ICorDebugModule2.ResolveAssembly");
            }

            assembly = RequirePointer(Volatile.Read(ref *assemblyAddress), "Runtime assembly resolution");
            return assembly;
        }
        catch
        {
            ReleasePointer(assembly);
            throw;
        }
        finally
        {
            ReleasePointer(module2);
        }
    }

    private static uint GetForwardedAssemblyReference(CorDebugLoadedModule module, string metadataName)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return 0;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
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
    /// Tries to resolve one exact metadata type and its owning runtime module.
    /// </summary>
    /// <param name="metadataName">The full metadata type name.</param>
    /// <param name="assemblyName">The optional simple assembly name.</param>
    /// <param name="resolvedModule">Receives the unique loaded runtime module.</param>
    /// <param name="resolvedToken">Receives the type-definition token.</param>
    /// <returns>True when exactly one matching loaded type was resolved.</returns>
    internal bool TryFindLoadedType(
        string metadataName,
        string? assemblyName,
        out CorDebugLoadedModule? resolvedModule,
        out uint resolvedToken)
    {
        resolvedModule = null;
        resolvedToken = 0;
        int scannedTypes = 0;
        var visitedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0; depth < MaximumForwardingDepth; depth++)
        {
            foreach (CorDebugLoadedModule module in _modules.GetRuntimeModules())
            {
                uint? match = TryFindTypeInModule(
                    module,
                    metadataName,
                    assemblyName,
                    ref scannedTypes);
                if (match is null)
                {
                    continue;
                }

                if (resolvedModule is not null)
                {
                    return false;
                }

                resolvedModule = module;
                resolvedToken = match.Value;
            }

            if (resolvedModule is not null)
            {
                return true;
            }

            if (assemblyName is null ||
                !visitedAssemblies.Add(assemblyName) ||
                !TryResolveForwardedAssembly(
                    metadataName,
                    assemblyName,
                    out string? forwardedAssembly))
            {
                return false;
            }

            assemblyName = forwardedAssembly;
        }

        return false;
    }

    private bool TryResolveForwardedAssembly(
        string metadataName,
        string assemblyName,
        out string? forwardedAssembly)
    {
        forwardedAssembly = null;
        foreach (string candidate in _modules.GetRuntimeModules()
            .Select(module => GetForwardedAssembly(module, metadataName, assemblyName))
            .OfType<string>())
        {
            if (forwardedAssembly is not null && !string.Equals(
                forwardedAssembly,
                candidate,
                StringComparison.OrdinalIgnoreCase))
            {
                forwardedAssembly = null;
                return false;
            }

            forwardedAssembly = candidate;
        }

        return forwardedAssembly is not null;
    }

    private static string? GetForwardedAssembly(
        CorDebugLoadedModule module,
        string metadataName,
        string assemblyName)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        if (!metadata.IsAssembly || !string.Equals(
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            assemblyName,
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? forwardedAssembly = null;
        foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
        {
            if (!string.Equals(
                GetExportedTypeName(metadata, handle),
                metadataName,
                StringComparison.Ordinal) ||
                !TryGetForwardedAssembly(metadata, handle, out string? candidate))
            {
                continue;
            }

            if (forwardedAssembly is not null && !string.Equals(
                forwardedAssembly,
                candidate,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            forwardedAssembly = candidate;
        }

        return forwardedAssembly;
    }

    private static string GetExportedTypeName(
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

    private static bool TryGetForwardedAssembly(
        MetadataReader metadata,
        ExportedTypeHandle handle,
        out string? assemblyName)
    {
        assemblyName = null;
        for (int depth = 0; depth < MaximumForwardingDepth; depth++)
        {
            ExportedType type = metadata.GetExportedType(handle);
            if (type.Implementation.Kind == HandleKind.ExportedType)
            {
                handle = (ExportedTypeHandle)type.Implementation;
                continue;
            }

            if (!type.IsForwarder ||
                type.Implementation.Kind != HandleKind.AssemblyReference)
            {
                return false;
            }

            AssemblyReference reference = metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)type.Implementation);
            assemblyName = metadata.GetString(reference.Name);
            return !string.IsNullOrEmpty(assemblyName);
        }

        throw new BadImageFormatException(
            $"An exported type exceeds {MaximumForwardingDepth} nested levels.");
    }

    private static uint? TryFindTypeInModule(
        CorDebugLoadedModule module,
        string metadataName,
        string? assemblyName,
        ref int scannedTypes)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
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
                    $"Results View resolution exceeds {MaximumTypeScanCount} loaded types.");
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

    private static string GetMetadataTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        if (!type.GetDeclaringType().IsNil)
        {
            return $"{GetMetadataTypeName(metadata, type.GetDeclaringType())}+{name}";
        }

        string typeNamespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace)
            ? name
            : $"{typeNamespace}.{name}";
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer
        : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void ReleasePointer(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
