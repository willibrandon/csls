using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical source breakpoints and owns their active runtime bindings.
/// </summary>
internal sealed partial class SourceBreakpointManager : IDisposable
{
    private const int HiddenSequencePointLine = 0x00feefee;
    private const int MaximumModuleCount = 4096;
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");
    private readonly Func<DebugSourceBreakpointInfo, CancellationToken, ValueTask> _notifyChanged;
    private readonly Dictionary<string, List<SourceBreakpointDefinition>> _definitions;
    private readonly Dictionary<nint, SourceBreakpointBinding> _bindings = [];
    private readonly Dictionary<nint, CorDebugLoadedModule> _modules = [];
    private int _nextBreakpointId;
    private int _nextModuleId;
    private int _disposed;

    /// <summary>
    /// Creates an empty manager that publishes verified binding changes.
    /// </summary>
    /// <param name="notifyChanged">The ordered breakpoint-change notification callback.</param>
    internal SourceBreakpointManager(
        Func<DebugSourceBreakpointInfo, CancellationToken, ValueTask> notifyChanged)
    {
        ArgumentNullException.ThrowIfNull(notifyChanged);
        _notifyChanged = notifyChanged;
        _definitions = new Dictionary<string, List<SourceBreakpointDefinition>>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>
    /// Replaces every logical breakpoint for one source document.
    /// </summary>
    /// <param name="sourcePath">The absolute source document path.</param>
    /// <param name="requests">The complete replacement request list.</param>
    /// <param name="cancellationToken">Cancels runtime rebinding.</param>
    /// <returns>The ordered current breakpoint snapshots.</returns>
    internal async ValueTask<IReadOnlyList<DebugSourceBreakpointInfo>> SetAsync(
        string sourcePath,
        IReadOnlyList<DebugSourceBreakpointRequest> requests,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(requests);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("A source breakpoint path must be absolute.", nameof(sourcePath));
        }

        string normalizedPath = Path.GetFullPath(sourcePath);
        RemoveBindings(normalizedPath);
        var definitions = new List<SourceBreakpointDefinition>(requests.Count);
        foreach (DebugSourceBreakpointRequest request in requests)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Line);
            if (request.Column is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    "A source breakpoint column must be positive when provided.");
            }

            definitions.Add(new SourceBreakpointDefinition
            {
                Id = checked(++_nextBreakpointId),
                SourcePath = normalizedPath,
                RequestedLine = request.Line,
                RequestedColumn = request.Column
            });
        }

        if (definitions.Count == 0)
        {
            _ = _definitions.Remove(normalizedPath);
            return [];
        }

        _definitions[normalizedPath] = definitions;
        foreach (CorDebugLoadedModule module in _modules.Values)
        {
            await BindModuleAsync(
                module,
                definitions,
                notifyChanges: false,
                cancellationToken).ConfigureAwait(false);
        }

        return definitions.Select(static definition => definition.ToInfo()).ToArray();
    }

    /// <summary>
    /// Retains a newly loaded module and resolves every matching pending breakpoint.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task that completes after applicable breakpoints are activated.</returns>
    internal async ValueTask LoadModuleAsync(nint module, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(module);
        if (_modules.Count == MaximumModuleCount)
        {
            throw new InvalidOperationException(
                $"The target exceeds the loaded-module limit of {MaximumModuleCount}.");
        }

        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        _ = ComAbi.AddRef(module);
        var loadedModule = new CorDebugLoadedModule
        {
            Id = checked(++_nextModuleId),
            Path = GetModulePath(module),
            Pointer = module,
            Identity = identity
        };
        if (!_modules.TryAdd(identity, loadedModule))
        {
            _ = ComAbi.Release(identity);
            _ = ComAbi.Release(module);
            return;
        }

        foreach (List<SourceBreakpointDefinition> definitions in _definitions.Values)
        {
            await BindModuleAsync(
                loadedModule,
                definitions,
                notifyChanges: true,
                cancellationToken).ConfigureAwait(false);
        }
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseRuntimeBindings();
        _definitions.Clear();
    }
}
