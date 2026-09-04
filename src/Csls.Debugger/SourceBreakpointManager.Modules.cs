using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Tracks loaded runtime modules and releases their debugger-owned resources.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
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

            RemoveHotReloadRemaps(identity);
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
        ClearSources();
        foreach (List<SourceBreakpointDefinition> definitions in _definitions.Values)
        {
            foreach (SourceBreakpointDefinition definition in definitions)
            {
                definition.ResolvedLine = null;
                definition.ResolvedColumn = null;
                definition.HitCondition?.Reset();
            }
        }
    }

    /// <summary>
    /// Resolves the logical definition for a runtime source-breakpoint callback.
    /// </summary>
    /// <param name="breakpoint">The borrowed ICorDebugBreakpoint pointer.</param>
    /// <returns>The owned definition, or null when the callback is unrecognized.</returns>
    internal IManagedBreakpointDefinition? FindDefinition(nint breakpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(breakpoint);
        nint identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
        try
        {
            return _bindings.TryGetValue(identity, out SourceBreakpointBinding? binding)
                ? binding.Definition
                : null;
        }
        finally
        {
            _ = ComAbi.Release(identity);
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
        _hotReloadRemaps.Clear();
        _steppingPolicyActivated = false;
    }

    private static string? GetModuleName(nint module)
    {
        try
        {
            string name = CorDebugModulePath.Get(module);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetModulePath(string? reportedName) =>
        reportedName is not null && Path.IsPathFullyQualified(reportedName)
            ? Path.GetFullPath(reportedName)
            : null;
}
