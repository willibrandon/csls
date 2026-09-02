using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical source breakpoints and owns their active runtime bindings.
/// </summary>
internal sealed class SourceBreakpointManager : IDisposable
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
    /// Removes runtime bindings and ownership for one unloading module.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task that completes after binding changes are published.</returns>
    internal async ValueTask UnloadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(module);
        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            if (!_modules.Remove(identity, out CorDebugLoadedModule? loadedModule))
            {
                return;
            }

            var affectedBreakpointIds = new HashSet<int>();
            foreach ((nint breakpointIdentity, SourceBreakpointBinding binding) in
                _bindings.ToArray())
            {
                if (binding.ModuleIdentity != identity)
                {
                    continue;
                }

                _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
                _ = ComAbi.Release(binding.Identity);
                _ = ComAbi.Release(binding.Breakpoint);
                _ = _bindings.Remove(breakpointIdentity);
                _ = affectedBreakpointIds.Add(binding.BreakpointId);
            }

            _ = ComAbi.Release(loadedModule.Identity);
            _ = ComAbi.Release(loadedModule.Pointer);
            foreach (int breakpointId in affectedBreakpointIds)
            {
                if (_bindings.Values.Any(binding => binding.BreakpointId == breakpointId))
                {
                    continue;
                }

                SourceBreakpointDefinition? definition = _definitions.Values
                    .SelectMany(static definitions => definitions)
                    .FirstOrDefault(candidate => candidate.Id == breakpointId);
                if (definition is null)
                {
                    continue;
                }

                definition.ResolvedLine = null;
                definition.ResolvedColumn = null;
                await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Releases runtime objects after a failed launch while retaining logical requests.
    /// </summary>
    internal void ResetRuntimeBindings()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ReleaseRuntimeBindings();
        foreach (List<SourceBreakpointDefinition> definitions in _definitions.Values)
        {
            foreach (SourceBreakpointDefinition definition in definitions)
            {
                definition.ResolvedLine = null;
                definition.ResolvedColumn = null;
            }
        }
    }

    /// <summary>
    /// Tests whether a runtime breakpoint callback belongs to an active source binding.
    /// </summary>
    /// <param name="breakpoint">The borrowed ICorDebugBreakpoint pointer.</param>
    /// <returns>True when the runtime breakpoint is owned by this manager.</returns>
    internal bool Contains(nint breakpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(breakpoint);
        nint identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
        try
        {
            return _bindings.ContainsKey(identity);
        }
        finally
        {
            _ = ComAbi.Release(identity);
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

    private async ValueTask BindModuleAsync(
        CorDebugLoadedModule module,
        IReadOnlyList<SourceBreakpointDefinition> definitions,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        string modulePath;
        try
        {
            modulePath = CorDebugModulePath.Get(module.Pointer);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        string pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return;
        }

        Dictionary<int, SourceBreakpointLocation> locations;
        try
        {
            locations = ResolveLocations(pdbPath, definitions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return;
        }

        foreach (SourceBreakpointDefinition definition in definitions)
        {
            if (!locations.TryGetValue(definition.Id, out SourceBreakpointLocation? location))
            {
                continue;
            }

            Bind(module, definition, location);
            bool firstResolution = definition.ResolvedLine is null;
            definition.ResolvedLine = location.Line;
            definition.ResolvedColumn = location.Column;
            if (notifyChanges && firstResolution)
            {
                await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<int, SourceBreakpointLocation> ResolveLocations(
        string pdbPath,
        IReadOnlyList<SourceBreakpointDefinition> definitions)
    {
        using FileStream stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        MetadataReader reader = provider.GetMetadataReader();
        var result = new Dictionary<int, SourceBreakpointLocation>();
        int rowNumber = 0;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            rowNumber++;
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                if (point.IsHidden || point.StartLine == HiddenSequencePointLine)
                {
                    continue;
                }

                DocumentHandle documentHandle = point.Document.IsNil ? method.Document : point.Document;
                if (documentHandle.IsNil)
                {
                    continue;
                }

                string documentPath = Path.GetFullPath(
                    reader.GetString(reader.GetDocument(documentHandle).Name));
                foreach (SourceBreakpointDefinition definition in definitions)
                {
                    if (!PathsEqual(documentPath, definition.SourcePath) ||
                        !IsBetterLocation(definition, point, result))
                    {
                        continue;
                    }

                    result[definition.Id] = new SourceBreakpointLocation(
                        checked((uint)MetadataTokens.GetToken(
                            MetadataTokens.MethodDefinitionHandle(rowNumber))),
                        checked((uint)point.Offset),
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine);
                }
            }
        }

        return result;
    }

    private static bool IsBetterLocation(
        SourceBreakpointDefinition definition,
        SequencePoint candidate,
        Dictionary<int, SourceBreakpointLocation> current)
    {
        bool candidateContainsLine = definition.RequestedLine >= candidate.StartLine &&
            definition.RequestedLine <= candidate.EndLine;
        if (!candidateContainsLine && candidate.StartLine < definition.RequestedLine)
        {
            return false;
        }

        if (!current.TryGetValue(definition.Id, out SourceBreakpointLocation? existing))
        {
            return true;
        }

        bool existingContainsLine = definition.RequestedLine >= existing.Line &&
            definition.RequestedLine <= existing.EndLine;
        if (candidateContainsLine != existingContainsLine)
        {
            return candidateContainsLine;
        }

        int candidateDistance = Math.Abs(candidate.StartLine - definition.RequestedLine);
        int existingDistance = Math.Abs(existing.Line - definition.RequestedLine);
        if (candidateDistance != existingDistance)
        {
            return candidateDistance < existingDistance;
        }

        int requestedColumn = definition.RequestedColumn ?? 0;
        return Math.Abs(candidate.StartColumn - requestedColumn) <
            Math.Abs(existing.Column - requestedColumn);
    }

    private unsafe void Bind(
        CorDebugLoadedModule module,
        SourceBreakpointDefinition definition,
        SourceBreakpointLocation location)
    {
        nint function = 0;
        nint code = 0;
        nint breakpoint = 0;
        nint identity = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugModuleAbi(module.Pointer).GetFunctionFromToken(
                    location.MethodToken,
                    (nint)functionAddress),
                "ICorDebugModule.GetFunctionFromToken");
            function = Volatile.Read(ref *functionAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            nint* breakpointAddress = &breakpoint;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(
                    location.IlOffset,
                    (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            _bindings.Add(identity, new SourceBreakpointBinding
            {
                BreakpointId = definition.Id,
                ModuleIdentity = module.Identity,
                Breakpoint = breakpoint,
                Identity = identity
            });
            breakpoint = 0;
            identity = 0;
        }
        finally
        {
            if (identity != 0)
            {
                _ = ComAbi.Release(identity);
            }

            if (breakpoint != 0)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
                _ = ComAbi.Release(breakpoint);
            }

            if (code != 0)
            {
                _ = ComAbi.Release(code);
            }

            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private void RemoveBindings(string sourcePath)
    {
        if (!_definitions.TryGetValue(sourcePath, out List<SourceBreakpointDefinition>? definitions))
        {
            return;
        }

        var ids = definitions.Select(static definition => definition.Id).ToHashSet();
        foreach ((nint identity, SourceBreakpointBinding binding) in _bindings.ToArray())
        {
            if (!ids.Contains(binding.BreakpointId))
            {
                continue;
            }

            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
            _ = ComAbi.Release(binding.Identity);
            _ = ComAbi.Release(binding.Breakpoint);
            _ = _bindings.Remove(identity);
        }
    }

    private void ReleaseRuntimeBindings()
    {
        foreach (SourceBreakpointBinding binding in _bindings.Values)
        {
            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
            _ = ComAbi.Release(binding.Identity);
            _ = ComAbi.Release(binding.Breakpoint);
        }

        _bindings.Clear();
        foreach (CorDebugLoadedModule module in _modules.Values)
        {
            _ = ComAbi.Release(module.Identity);
            _ = ComAbi.Release(module.Pointer);
        }

        _modules.Clear();
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
