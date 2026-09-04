namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries one compiler-produced managed Hot Reload module update.
/// </summary>
/// <param name="StopGeneration">The exact stopped generation authorizing the update.</param>
/// <param name="ModuleId">The stable session-local target module identifier.</param>
/// <param name="ExpectedModuleGeneration">The module generation used to compile the update.</param>
/// <param name="MetadataDelta">The immutable ECMA-335 metadata delta.</param>
/// <param name="IlDelta">The immutable managed IL delta.</param>
/// <param name="PdbDelta">The immutable minimal Portable PDB delta.</param>
/// <param name="ActiveStatements">The compiler-produced active-statement remap set.</param>
public sealed record DebugHotReloadRequest(
    long StopGeneration,
    int ModuleId,
    int ExpectedModuleGeneration,
    ReadOnlyMemory<byte> MetadataDelta,
    ReadOnlyMemory<byte> IlDelta,
    ReadOnlyMemory<byte> PdbDelta,
    IReadOnlyList<DebugHotReloadActiveStatement> ActiveStatements);
