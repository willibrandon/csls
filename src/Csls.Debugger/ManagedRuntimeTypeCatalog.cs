using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves metadata types through exact runtime modules and assembly bindings.
/// </summary>
internal sealed class ManagedRuntimeTypeCatalog
{
    private const int MaximumForwardingDepth = 256;
    private const int CannotResolveAssembly = unchecked((int)0x80131C11);
    private readonly SourceBreakpointManager _modules;
    private readonly ManagedScopedTypeResolver _scopedTypes;

    /// <summary>
    /// Creates a runtime type catalog over the loaded module set.
    /// </summary>
    /// <param name="modules">The loaded runtime-module catalog.</param>
    internal ManagedRuntimeTypeCatalog(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
        _scopedTypes = new ManagedScopedTypeResolver(
            module => _modules.FindModule(module)?.OpenPeReader(), GetReferencedAssembly);
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

        if (!_scopedTypes.TryResolve(signature, out nint module, out resolvedToken))
        {
            return false;
        }
        try
        {
            resolvedModule = _modules.FindModule(module);
            return resolvedModule is not null;
        }
        finally
        {
            ReleasePointer(module);
        }
    }

    private unsafe nint GetReferencedAssembly(nint module, uint referenceToken)
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
                var runtimeModule = new ICorDebugModule2Abi(module2);
                int result = runtimeModule.ResolveAssembly(referenceToken, (nint)assemblyAddress);
                assembly = Volatile.Read(ref *assemblyAddress);
                if (result == CannotResolveAssembly && _modules.FindModule(module) is { MetadataDeltas.Count: > 0 } loaded)
                {
                    // An added AssemblyRef can be cold while an identical older reference is already bound.
                    foreach (uint equivalent in ManagedAssemblyReferenceResolver.FindEquivalentReferences(loaded, referenceToken))
                    {
                        ReleasePointer(assembly);
                        assembly = 0;
                        result = runtimeModule.ResolveAssembly(equivalent, (nint)assemblyAddress);
                        assembly = Volatile.Read(ref *assemblyAddress);
                        if (result != CannotResolveAssembly)
                        {
                            break;
                        }
                    }
                }

                CorDebugHResult.ThrowIfFailed(result, "ICorDebugModule2.ResolveAssembly");
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
                ManagedMetadataTypeLookup.GetExportedTypeName(metadata, handle),
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

        return ManagedMetadataTypeLookup.FindDefinition(
            peReader.GetMetadataReader(), metadataName, assemblyName, ref scannedTypes);
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
