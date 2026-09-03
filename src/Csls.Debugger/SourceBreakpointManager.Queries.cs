using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Answers bounded source-breakpoint module queries.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
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
            result.Add(new DebugModuleInfo(
                module.Id,
                module.Path is null
                    ? $"Dynamic module {module.Id}"
                    : Path.GetFileName(module.Path),
                module.Path,
                module.SymbolKind,
                module.SymbolPath,
                module.IsOptimized,
                module.OptimizationDiagnostic));
        }

        return result;
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
