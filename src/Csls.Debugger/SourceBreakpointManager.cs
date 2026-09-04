using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical source breakpoints and owns their active runtime bindings.
/// </summary>
internal sealed partial class SourceBreakpointManager : IDisposable
{
    private const int MaximumModuleCount = 4096;
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");
    private readonly Func<DebugSourceBreakpointInfo, CancellationToken, ValueTask> _notifyChanged;
    private readonly Dictionary<string, List<SourceBreakpointDefinition>> _definitions;
    private readonly Dictionary<nint, SourceBreakpointBinding> _bindings = [];
    private readonly Dictionary<nint, CorDebugLoadedModule> _modules = [];
    private readonly Dictionary<(nint ModuleIdentity, uint MethodToken, int MethodVersion,
        uint OldIlOffset), uint> _hotReloadRemaps = [];
    private readonly DebugSymbolLocator _symbolLocator = new();
    private IReadOnlyList<string> _hotReloadCapabilities = [];
    private int _nextBreakpointId;
    private int _nextModuleId;
    private bool _enableHotReload;
    private bool _suppressJitOptimizations;
    private bool _justMyCode = true;
    private bool _enableStepFiltering = true;
    private bool _steppingPolicyActivated;
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
    /// Sets the exact target-runtime capability set before modules are loaded.
    /// </summary>
    /// <param name="runtimeVersion">The CoreCLR product version, when available.</param>
    internal void SetRuntimeVersion(Version? runtimeVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_modules.Count != 0)
        {
            throw new InvalidOperationException(
                "Debugger runtime capabilities cannot change after modules have loaded.");
        }

        _hotReloadCapabilities = HotReloadRuntimeCapabilities.Get(runtimeVersion);
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
        if (!SourcePathMapper.IsAbsolutePath(sourcePath))
        {
            throw new ArgumentException("A source breakpoint path must be absolute.", nameof(sourcePath));
        }

        string normalizedPath = SourcePathMapper.NormalizePath(sourcePath);
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

            bool validHitCondition = DebugHitCondition.TryParse(
                request.HitCondition,
                out DebugHitCondition? hitCondition);
            definitions.Add(new SourceBreakpointDefinition
            {
                Id = checked(++_nextBreakpointId),
                SourcePath = normalizedPath,
                RequestedLine = request.Line,
                RequestedColumn = request.Column,
                Condition = NormalizeOptionalExpression(request.Condition),
                HitCondition = hitCondition,
                LogMessage = NormalizeOptionalExpression(request.LogMessage),
                ValidationMessage = validHitCondition
                    ? null
                    : DebugHitCondition.ValidationErrorMessage
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
        string? reportedName = GetModuleName(module);
        string? modulePath = GetModulePath(reportedName);
        bool isInMemory = IsInMemoryModule(module);
        bool isDynamic = IsDynamicModule(module);
        (
            bool? isOptimized,
            string? optimizationDiagnostic,
            bool? isHotReloadEnabled,
            string? hotReloadDiagnostic) = ConfigureJitPolicy(module, isDynamic);
        DebugSymbolResolution? symbols = modulePath is null
            ? null
            : await _symbolLocator.ResolveAsync(modulePath, cancellationToken)
                .ConfigureAwait(false);
        (DebugModuleSymbolKind symbolKind, string? symbolPath) = GetSymbolInfo(symbols);
        var loadedModule = new CorDebugLoadedModule
        {
            Id = checked(++_nextModuleId),
            Name = modulePath is null ? reportedName : Path.GetFileName(modulePath),
            Path = modulePath,
            Pointer = module,
            Identity = identity,
            SymbolKind = symbolKind,
            SymbolPath = symbolPath,
            SymbolsInspected = true,
            IsInMemory = isInMemory,
            IsDynamic = isDynamic,
            ModuleImage = isInMemory ? CorDebugModuleImageReader.TryRead(module) : null,
            IsOptimized = isOptimized,
            OptimizationDiagnostic = optimizationDiagnostic,
            IsHotReloadEnabled = isHotReloadEnabled,
            HotReloadDiagnostic = hotReloadDiagnostic,
            HotReloadCapabilities = isHotReloadEnabled == true
                ? _hotReloadCapabilities
                : []
        };
        if (!_modules.TryAdd(identity, loadedModule))
        {
            _ = ComAbi.Release(identity);
            _ = ComAbi.Release(module);
            return;
        }

        await RefreshInMemorySymbolsAsync(
            loadedModule,
            notifyChanges: true,
            cancellationToken).ConfigureAwait(false);

        if (_steppingPolicyActivated)
        {
            ConfigureJustMyCode(loadedModule);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseRuntimeBindings();
        _definitions.Clear();
        ClearSources();
    }

    private static string? NormalizeOptionalExpression(string? expression) =>
        string.IsNullOrWhiteSpace(expression) ? null : expression;
}
