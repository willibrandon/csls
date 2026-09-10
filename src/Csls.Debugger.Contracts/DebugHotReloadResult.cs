namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one successfully applied managed Hot Reload module update.
/// </summary>
/// <param name="ModuleId">The stable session-local target module identifier.</param>
/// <param name="ModuleGeneration">The newly committed module generation.</param>
/// <param name="StopGeneration">The new stopped generation owning debugger handles.</param>
/// <param name="UpdatedMethods">The aggregate metadata tokens with updated debug information.</param>
/// <param name="UpdatedTypes">The validated aggregate type-definition tokens.</param>
public sealed record DebugHotReloadResult(
    int ModuleId,
    int ModuleGeneration,
    long StopGeneration,
    IReadOnlyList<uint> UpdatedMethods,
    IReadOnlyList<uint> UpdatedTypes);
