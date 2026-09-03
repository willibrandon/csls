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

        return modules.Select(static module =>
        {
            DebugModuleSymbolKind symbolKind = DebugModuleSymbolKind.None;
            string? symbolPath = null;
            if (module.Path is not null)
            {
                try
                {
                    using var symbols = PortablePdbReader.TryOpen(module.Path);
                    if (symbols is not null)
                    {
                        symbolKind = symbols.StorageKind == PortablePdbStorageKind.Embedded
                            ? DebugModuleSymbolKind.EmbeddedPortablePdb
                            : DebugModuleSymbolKind.PortablePdb;
                        symbolPath = symbols.Path;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        BadImageFormatException)
                {
                }
            }

            return new DebugModuleInfo(
                module.Id,
                module.Path is null
                    ? $"Dynamic module {module.Id}"
                    : Path.GetFileName(module.Path),
                module.Path,
                symbolKind,
                symbolPath);
        }).ToArray();
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
