using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves an executable's managed entry token to its first authored sequence point.
/// </summary>
internal static class EntryPointLocationResolver
{
    /// <summary>
    /// Resolves the executable entry, including compiler-generated async entry wrappers.
    /// </summary>
    /// <param name="module">The loaded module and its validated symbol sources.</param>
    /// <param name="cancellationToken">Cancels symbol traversal.</param>
    /// <returns>The entry method and IL offset, or null for a module without a managed entry.</returns>
    internal static (uint MethodToken, uint IlOffset)? Resolve(
        CorDebugLoadedModule module,
        CancellationToken cancellationToken)
    {
        using PEReader? pe = module.OpenPeReader();
        CorHeader? header = pe?.PEHeaders.CorHeader;
        if (pe is null || header is null ||
            (header.Flags & CorFlags.NativeEntryPoint) != 0 ||
            (header.EntryPointTokenOrRelativeVirtualAddress & 0xff000000) != 0x06000000)
        {
            return null;
        }

        uint entryToken = checked((uint)header.EntryPointTokenOrRelativeVirtualAddress);
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition entry = GetMethod(metadata, entryToken);
        using DebugSymbolReader? symbols = module.OpenSymbols();
        if (symbols is null)
        {
            return (entryToken, 0);
        }

        uint debugToken = symbols.GetEntryPoint() ?? entryToken;
        _ = GetMethod(metadata, debugToken);
        var points = new Dictionary<uint, ManagedSequencePoint>();
        foreach (ManagedSequencePoint point in symbols.GetSequencePoints(null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!points.TryAdd(point.MethodToken, point))
            {
                continue;
            }

            uint? kickoff = symbols.GetStateMachineKickoffMethod(point.MethodToken);
            if (kickoff is uint kickoffToken)
            {
                _ = points.TryAdd(kickoffToken, point);
            }
        }

        if (points.TryGetValue(debugToken, out ManagedSequencePoint? visible))
        {
            return (visible.MethodToken, checked((uint)visible.IlOffset));
        }

        if (entry.RelativeVirtualAddress != 0)
        {
            ManagedSequencePoint? asyncEntry = null;
            byte[] il = pe.GetMethodBody(entry.RelativeVirtualAddress).GetILBytes() ?? [];
            foreach (ManagedIlInstruction instruction in ManagedIlDecoder.Decode(il))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (instruction.Name != "call" || instruction.MetadataToken is not int token ||
                    (token & 0xff000000) != 0x06000000 ||
                    !points.TryGetValue(checked((uint)token), out ManagedSequencePoint? candidate) ||
                    symbols.GetStateMachineKickoffMethod(candidate.MethodToken) != (uint)token ||
                    GetMethod(metadata, checked((uint)token)).GetDeclaringType() != entry.GetDeclaringType())
                {
                    continue;
                }

                if (asyncEntry is not null)
                {
                    return (entryToken, 0);
                }

                asyncEntry = candidate;
            }

            if (asyncEntry is not null)
            {
                return (asyncEntry.MethodToken, checked((uint)asyncEntry.IlOffset));
            }
        }

        return (entryToken, 0);
    }

    private static MethodDefinition GetMethod(MetadataReader metadata, uint token)
    {
        int row = checked((int)(token & 0x00ffffff));
        if ((token & 0xff000000) != 0x06000000 || row == 0 || row > metadata.MethodDefinitions.Count)
        {
            throw new BadImageFormatException("The entry point is outside the module's method table.");
        }

        return metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
    }
}
