using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Identifies equivalent assembly references within one module without changing its runtime binding scope.
/// </summary>
internal static class ManagedAssemblyReferenceResolver
{
    /// <summary>
    /// Opens current module metadata and returns equivalent reference tokens in aggregate row order.
    /// </summary>
    internal static IReadOnlyList<uint> FindEquivalentReferences(CorDebugLoadedModule module, uint referenceToken)
    {
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            throw new InvalidOperationException("The assembly reference's runtime metadata is unavailable.");
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        EntityHandle handle = MetadataTokens.EntityHandle(checked((int)referenceToken));
        if (handle.Kind != HandleKind.AssemblyReference)
        {
            throw new BadImageFormatException("The runtime assembly reference is not an AssemblyRef token.");
        }

        return [.. FindEquivalentReferences(metadata, (AssemblyReferenceHandle)handle)
            .Select(static candidate => checked((uint)MetadataTokens.GetToken(candidate)))];
    }

    /// <summary>
    /// Finds other references with identical name, version, culture, flags, and public key or token.
    /// </summary>
    internal static IReadOnlyList<AssemblyReferenceHandle> FindEquivalentReferences(
        ManagedMetadataImage metadata, AssemblyReferenceHandle handle)
    {
        if (handle.IsNil)
        {
            throw new BadImageFormatException("An assembly reference must identify a nonzero metadata row.");
        }

        AssemblyReference reference = metadata.GetAssemblyReference(handle);
        string name = metadata.GetString(reference.Name);
        string culture = metadata.GetString(reference.Culture);
        BlobReader key = metadata.GetBlobReader(reference.PublicKeyOrToken);
        byte[] keyBytes = key.ReadBytes(key.Length);
        List<AssemblyReferenceHandle> matches = [];
        foreach (AssemblyReferenceHandle candidateHandle in metadata.GetAssemblyReferences())
        {
            if (candidateHandle == handle)
            {
                continue;
            }

            AssemblyReference candidate = metadata.GetAssemblyReference(candidateHandle);
            if (reference.Version != candidate.Version || reference.Flags != candidate.Flags ||
                !string.Equals(name, metadata.GetString(candidate.Name), StringComparison.Ordinal) ||
                !string.Equals(culture, metadata.GetString(candidate.Culture), StringComparison.Ordinal))
            {
                continue;
            }

            BlobReader candidateKey = metadata.GetBlobReader(candidate.PublicKeyOrToken);
            if (keyBytes.Length == candidateKey.Length &&
                keyBytes.AsSpan().SequenceEqual(candidateKey.ReadBytes(candidateKey.Length)))
            {
                matches.Add(candidateHandle);
            }
        }

        return matches;
    }
}
