using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Answers bounded source-breakpoint module queries.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Gets every logical source breakpoint ordered by session-local identifier.
    /// </summary>
    internal IReadOnlyList<DebugSourceBreakpointInfo> GetBreakpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _definitions.Values
            .SelectMany(static definitions => definitions)
            .OrderBy(static definition => definition.Id)
            .Select(static definition => definition.ToInfo())
            .ToArray();
    }

    /// <summary>
    /// Determines whether a symbol document resolves to an absolute client source path.
    /// </summary>
    /// <param name="symbolPath">The path recorded in the selected managed PDB.</param>
    /// <param name="clientPath">The absolute path supplied by the client.</param>
    /// <returns>True when source path mapping identifies the same document.</returns>
    internal bool PathsReferToSameSource(string symbolPath, string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolPath);
        string normalizedClient = NormalizeAbsolutePath(clientPath);
        return PathsEqual(_sourcePathMapper.Map(symbolPath), normalizedClient);
    }

    /// <summary>
    /// Gets a stable ordered page of modules observed through runtime callbacks.
    /// </summary>
    /// <param name="start">The zero-based first module to return.</param>
    /// <param name="count">The maximum count, or zero for all remaining modules.</param>
    /// <returns>The requested loaded-module page.</returns>
    internal IReadOnlyList<DebugModuleInfo> GetModules(int start, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        IEnumerable<CorDebugLoadedModule> modules = _modules.Values
            .OrderBy(static module => module.Id)
            .Skip(start);
        if (count > 0)
        {
            modules = modules.Take(count);
        }

        var result = new List<DebugModuleInfo>();
        foreach (CorDebugLoadedModule module in modules)
        {
            EnsureSymbolsInspected(module);
            ClassifyUserCode(module);
            result.Add(new DebugModuleInfo(
                module.Id,
                module.Name ?? $"Dynamic module {module.Id}",
                module.Path,
                module.SymbolKind,
                module.SymbolPath,
                module.IsHotReloadEnabled,
                module.HotReloadGeneration,
                module.HotReloadDiagnostic,
                module.IsOptimized,
                module.OptimizationDiagnostic,
                module.IsUserCode,
                module.JustMyCodeDiagnostic));
        }

        return result;
    }

    /// <summary>
    /// Gets the ordered retained runtime modules for serialized engine evaluation.
    /// </summary>
    /// <returns>A shallow snapshot whose pointers remain owned by this manager.</returns>
    internal IReadOnlyList<CorDebugLoadedModule> GetRuntimeModules()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return [.. _modules.Values.OrderBy(static module => module.Id)];
    }

    /// <summary>
    /// Gets the number of modules observed through runtime callbacks.
    /// </summary>
    internal int ModuleCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _modules.Count;
        }
    }
}
