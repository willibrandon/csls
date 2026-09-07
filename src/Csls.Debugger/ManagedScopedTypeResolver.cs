using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves scoped type definitions through owned runtime assembly and module references.
/// </summary>
internal sealed class ManagedScopedTypeResolver
{
    private const int MaximumForwardingDepth = 256;
    private const int MaximumModuleCount = 4096;
    private readonly Func<nint, PEReader?> _openModule;
    private readonly Func<nint, uint, nint> _resolveAssembly;

    /// <summary>
    /// Binds exact runtime assembly resolution to the owning session's metadata snapshots.
    /// </summary>
    /// <param name="openModule">Opens an owned metadata reader for a borrowed module.</param>
    /// <param name="resolveAssembly">Returns an owned assembly for a borrowed module and its AssemblyRef token.</param>
    internal ManagedScopedTypeResolver(Func<nint, PEReader?> openModule, Func<nint, uint, nint> resolveAssembly)
    {
        ArgumentNullException.ThrowIfNull(openModule);
        ArgumentNullException.ThrowIfNull(resolveAssembly);
        _openModule = openModule;
        _resolveAssembly = resolveAssembly;
    }

    /// <summary>
    /// Finds a scoped definition and transfers its module reference to the caller.
    /// </summary>
    /// <param name="signature">The metadata signature preserving its borrowed source module.</param>
    /// <param name="module">Receives an owned module reference on success, or zero.</param>
    /// <param name="token">Receives the exact type-definition token on success, or zero.</param>
    /// <returns>Whether the signature resolves to one definition in its bound runtime assembly.</returns>
    internal bool TryResolve(ManagedMetadataTypeSignature signature, out nint module, out uint token)
    {
        ArgumentNullException.ThrowIfNull(signature);
        module = 0;
        token = 0;
        if (signature.SourceModule == 0 || signature.MetadataName is not string name)
        {
            return false;
        }
        if (signature.DefinitionToken != 0)
        {
            _ = ComAbi.AddRef(signature.SourceModule);
            module = signature.SourceModule;
            token = signature.DefinitionToken;
            return true;
        }

        nint assembly = _resolveAssembly(signature.SourceModule, signature.AssemblyReferenceToken);
        HashSet<nint> visited = [];
        try
        {
            int scannedTypes = 0;
            if (FindTypeInAssembly(RequirePointer(assembly, "Runtime assembly resolution"), name,
                visited, 0, ref scannedTypes) is not { } match)
            {
                return false;
            }
            module = match.Module;
            token = match.Token;
            return true;
        }
        finally
        {
            ReleaseAll(visited);
            Release(assembly);
        }
    }

    private (nint Module, uint Token)? FindTypeInAssembly(nint assembly, string name,
        HashSet<nint> visited, int depth, ref int scannedTypes)
    {
        if (depth >= MaximumForwardingDepth)
        {
            throw new BadImageFormatException("A runtime type exceeds the supported forwarding depth.");
        }
        nint identity = ComAbi.GetIdentity(assembly);
        if (!visited.Add(identity))
        {
            Release(identity);
            return null;
        }

        List<nint> modules = GetAssemblyModules(assembly);
        try
        {
            (nint Module, uint Token)? result = null;
            foreach (nint module in modules)
            {
                using PEReader? image = _openModule(module);
                uint? match = image is { HasMetadata: true }
                    ? ManagedMetadataTypeLookup.FindDefinition(image.GetMetadataReader(), name, null, ref scannedTypes)
                    : null;
                if (match is null)
                {
                    continue;
                }
                if (result is not null)
                {
                    return null;
                }
                result = (module, match.Value);
            }
            if (result is { } definition)
            {
                _ = ComAbi.AddRef(definition.Module);
                return definition;
            }
            foreach (nint module in modules)
            {
                uint reference;
                using (PEReader? image = _openModule(module))
                {
                    reference = image is { HasMetadata: true }
                        ? ManagedMetadataTypeLookup.GetForwardedAssemblyReference(image.GetMetadataReader(), name)
                        : 0;
                }
                if (reference == 0)
                {
                    continue;
                }
                nint forwardedAssembly = _resolveAssembly(module, reference);
                try
                {
                    if (FindTypeInAssembly(RequirePointer(forwardedAssembly, "Runtime assembly resolution"),
                        name, visited, depth + 1, ref scannedTypes) is { } match)
                    {
                        return match;
                    }
                }
                finally
                {
                    Release(forwardedAssembly);
                }
            }
            return null;
        }
        finally
        {
            ReleaseAll(modules);
        }
    }

    private static unsafe List<nint> GetAssemblyModules(nint assembly)
    {
        nint enumerator = 0;
        List<nint> result = [];
        try
        {
            int hr = new ICorDebugAssemblyAbi(assembly).EnumerateModules((nint)(&enumerator));
            enumerator = Volatile.Read(ref enumerator);
            CorDebugHResult.ThrowIfFailed(hr, "ICorDebugAssembly.EnumerateModules");
            var values = new ICorDebugModuleEnumAbi(RequirePointer(enumerator, "ICorDebugAssembly.EnumerateModules"));
            for (int index = 0; index <= MaximumModuleCount; index++)
            {
                nint module = 0;
                try
                {
                    uint fetched = 0;
                    hr = values.Next(1, (nint)(&module), (nint)(&fetched));
                    module = Volatile.Read(ref module);
                    CorDebugHResult.ThrowIfFailed(hr, "ICorDebugModuleEnum.Next");
                    if (Volatile.Read(ref fetched) == 0)
                    {
                        return result;
                    }
                    if (index == MaximumModuleCount)
                    {
                        break;
                    }
                    result.Add(RequirePointer(module, "ICorDebugModuleEnum.Next"));
                    module = 0;
                }
                finally
                {
                    Release(module);
                }
            }
            throw new InvalidOperationException($"A runtime assembly exceeds {MaximumModuleCount} modules.");
        }
        catch
        {
            ReleaseAll(result);
            throw;
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void ReleaseAll(IEnumerable<nint> pointers)
    {
        foreach (nint pointer in pointers)
        {
            Release(pointer);
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
