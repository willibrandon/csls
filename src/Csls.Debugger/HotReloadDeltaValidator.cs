using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Validates bounded compiler deltas against one exact loaded module generation.
/// </summary>
internal static class HotReloadDeltaValidator
{
    private const int MaximumIlDeltaBytes = 256 * 1024 * 1024;
    private const int MaximumMetadataDeltaBytes = 64 * 1024 * 1024;
    private const int MaximumPdbDeltaBytes = 64 * 1024 * 1024;
    private const int MaximumActiveStatementCount = 65_536;

    /// <summary>
    /// Validates one metadata, IL, and Portable PDB delta set before runtime mutation.
    /// </summary>
    /// <param name="module">The retained runtime module and its committed generations.</param>
    /// <param name="metadataDelta">The candidate ECMA-335 metadata delta.</param>
    /// <param name="ilDelta">The candidate managed IL delta.</param>
    /// <param name="pdbDelta">The candidate minimal Portable PDB delta.</param>
    /// <param name="activeStatements">The compiler-produced active-statement updates.</param>
    /// <returns>The validated updated methods and exact runtime remap decisions.</returns>
    internal static HotReloadValidationResult Validate(
        CorDebugLoadedModule module,
        byte[] metadataDelta,
        byte[] ilDelta,
        byte[] pdbDelta,
        IReadOnlyList<DebugHotReloadActiveStatement> activeStatements)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(activeStatements);
        ValidateSize(metadataDelta, MaximumMetadataDeltaBytes, nameof(metadataDelta));
        ValidateSize(ilDelta, MaximumIlDeltaBytes, nameof(ilDelta));
        ValidateSize(pdbDelta, MaximumPdbDeltaBytes, nameof(pdbDelta));

        ValidateMetadata(module, metadataDelta);
        using DebugSymbolReader symbols = OpenSymbols(module, pdbDelta);
        using PortablePdbReader pdb = PortablePdbReader.TryOpen(pdbDelta)
            ?? throw new BadImageFormatException("The Hot Reload PDB delta is not portable.");
        IReadOnlyList<uint> updatedMethods = [.. pdb.Metadata
            .GetEditAndContinueMapEntries()
            .Where(static handle => handle.Kind == HandleKind.MethodDebugInformation)
            .Select(static handle => checked((uint)MetadataTokens.GetToken(
                ((MethodDebugInformationHandle)handle).ToDefinitionHandle())))
            .Distinct()
            .Order()];
        List<HotReloadActiveStatementRemap> remaps = ValidateActiveStatements(
            symbols,
            updatedMethods,
            activeStatements);
        return new HotReloadValidationResult(updatedMethods, remaps);
    }

    private static void ValidateMetadata(
        CorDebugLoadedModule module,
        byte[] metadataDelta)
    {
        using PEReader basePe = module.OpenPeReader()
            ?? throw new InvalidOperationException(
                $"Module {module.Id} has no readable baseline metadata image.");
        MetadataReader baseMetadata = basePe.GetMetadataReader();
        var providers = new List<MetadataReaderProvider>(module.MetadataDeltas.Count + 1);
        var readers = new List<MetadataReader>(module.MetadataDeltas.Count + 1);
        try
        {
            foreach (byte[] image in module.MetadataDeltas.Append(metadataDelta))
            {
                var provider = MetadataReaderProvider.FromMetadataImage(
                    ImmutableCollectionsMarshal.AsImmutableArray(image));
                providers.Add(provider);
                readers.Add(provider.GetMetadataReader());
            }

            var aggregator = new MetadataAggregator(baseMetadata, readers);
            MetadataReader candidate = readers[^1];
            ModuleDefinition definition = candidate.GetModuleDefinition();
            int expectedGeneration = checked(module.HotReloadGeneration + 1);
            if (definition.Generation != expectedGeneration)
            {
                throw new BadImageFormatException(
                    $"The Hot Reload metadata generation is {definition.Generation}; " +
                    $"expected {expectedGeneration}.");
            }

            Guid baselineModuleId = baseMetadata.GetGuid(
                baseMetadata.GetModuleDefinition().Mvid);
            Guid deltaModuleId = ReadAggregateGuid(
                definition.Mvid,
                aggregator,
                baseMetadata,
                readers);
            if (deltaModuleId != baselineModuleId)
            {
                throw new BadImageFormatException(
                    "The Hot Reload metadata delta belongs to a different module.");
            }

            Guid generationId = ReadAggregateGuid(
                definition.GenerationId,
                aggregator,
                baseMetadata,
                readers);
            if (generationId == Guid.Empty)
            {
                throw new BadImageFormatException(
                    "The Hot Reload metadata delta has no generation identifier.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new BadImageFormatException(
                "The Hot Reload metadata is not a valid minimal delta chain.",
                exception);
        }
        finally
        {
            foreach (MetadataReaderProvider provider in providers)
            {
                provider.Dispose();
            }
        }
    }

    private static DebugSymbolReader OpenSymbols(
        CorDebugLoadedModule module,
        byte[] pdbDelta)
    {
        IReadOnlyList<byte[]> deltas = [.. module.SymbolDeltas, pdbDelta];
        DebugSymbolReader? symbols = module.SymbolImage is not null
            ? DebugSymbolReader.TryOpen(module.SymbolImage, deltas)
            : module.Path is null
                ? null
                : DebugSymbolReader.TryOpen(module.Path, module.SymbolPath, deltas);
        return symbols ?? throw new InvalidOperationException(
            $"Module {module.Id} has no identity-matched base Portable PDB.");
    }

    private static List<HotReloadActiveStatementRemap> ValidateActiveStatements(
        DebugSymbolReader symbols,
        IReadOnlyList<uint> updatedMethods,
        IReadOnlyList<DebugHotReloadActiveStatement> activeStatements)
    {
        if (activeStatements.Count > MaximumActiveStatementCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeStatements),
                $"A Hot Reload update may map at most {MaximumActiveStatementCount} active statements.");
        }

        var updated = updatedMethods.ToHashSet();
        var seen = new HashSet<(uint MethodToken, int MethodVersion, uint OldIlOffset)>();
        var remaps = new List<HotReloadActiveStatementRemap>(activeStatements.Count);
        foreach (DebugHotReloadActiveStatement active in activeStatements)
        {
            ValidateActiveStatement(active, updated);
            if (!seen.Add((active.MethodToken, active.MethodVersion, active.OldIlOffset)))
            {
                throw new ArgumentException(
                    "A Hot Reload update contains a duplicate active statement.",
                    nameof(activeStatements));
            }

            uint[] offsets = [.. symbols.GetSequencePoints(active.MethodToken)
                .Where(point => MatchesUpdatedSpan(point, active))
                .Select(static point => checked((uint)point.IlOffset))
                .Distinct()
                .Take(2)];
            if (offsets.Length != 1)
            {
                throw new BadImageFormatException(
                    $"The updated symbols do not identify exactly one active statement for " +
                    $"method 0x{active.MethodToken:X8} at the supplied source span.");
            }

            remaps.Add(new HotReloadActiveStatementRemap(
                active.MethodToken,
                active.MethodVersion,
                active.OldIlOffset,
                offsets[0]));
        }

        return remaps;
    }

    private static void ValidateActiveStatement(
        DebugHotReloadActiveStatement active,
        HashSet<uint> updatedMethods)
    {
        if ((active.MethodToken & 0xff000000) != 0x06000000 ||
            !updatedMethods.Contains(active.MethodToken))
        {
            throw new ArgumentException(
                $"Active statement method 0x{active.MethodToken:X8} is not updated by this generation.",
                nameof(active));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(active.MethodVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(active.StartLine);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(active.StartLine, int.MaxValue - 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(active.StartColumn, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(active.StartColumn, int.MaxValue - 1);
        ArgumentOutOfRangeException.ThrowIfNegative(active.EndLine);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(active.EndLine, int.MaxValue - 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(active.EndColumn, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(active.EndColumn, int.MaxValue - 1);
        if ((active.StartColumn == -1) != (active.EndColumn == -1) ||
            active.EndLine < active.StartLine ||
            active.EndLine == active.StartLine && active.StartColumn >= 0 &&
            active.EndColumn < active.StartColumn)
        {
            throw new ArgumentException(
                "A Hot Reload active statement has an invalid updated source span.",
                nameof(active));
        }
    }

    private static bool MatchesUpdatedSpan(
        ManagedSequencePoint point,
        DebugHotReloadActiveStatement active) =>
        point.StartLine == active.StartLine + 1 &&
        point.EndLine == active.EndLine + 1 &&
        (active.StartColumn < 0 ||
            point.StartColumn == active.StartColumn + 1 &&
            point.EndColumn == active.EndColumn + 1);

    private static Guid ReadAggregateGuid(
        GuidHandle handle,
        MetadataAggregator aggregator,
        MetadataReader baseMetadata,
        List<MetadataReader> deltas)
    {
        var relativeHandle = (GuidHandle)aggregator.GetGenerationHandle(
            handle,
            out int generation);
        MetadataReader owner = generation == 0 ? baseMetadata : deltas[generation - 1];
        return owner.GetGuid(relativeHandle);
    }

    private static void ValidateSize(byte[] bytes, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bytes, parameterName);
        if (bytes.Length == 0 || bytes.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"A Hot Reload delta must contain between 1 and {maximum} bytes.");
        }
    }
}
