using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Tracks modules used to resolve managed function breakpoints.
/// </summary>
internal sealed partial class FunctionBreakpointManager
{
    /// <summary>
    /// Retains a loaded module and binds matching function breakpoints.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notification.</param>
    /// <returns>A task that completes after applicable methods are bound.</returns>
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
            Id = 0,
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

        await BindModuleAsync(loadedModule, notifyChanges: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes bindings and ownership for an unloading module.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notification.</param>
    /// <returns>A task that completes after binding changes are published.</returns>
    internal async ValueTask UnloadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            if (!_modules.Remove(identity, out CorDebugLoadedModule? loadedModule))
            {
                return;
            }

            var changed = new HashSet<int>();
            foreach ((nint bindingIdentity, FunctionBreakpointBinding binding) in
                _bindings.ToArray())
            {
                if (binding.ModuleIdentity != identity)
                {
                    continue;
                }

                ReleaseBinding(binding);
                _ = _bindings.Remove(bindingIdentity);
                FunctionBreakpointDefinition definition = _definitions.Single(
                    candidate => candidate.Id == binding.BreakpointId);
                definition.BindingCount--;
                _ = changed.Add(definition.Id);
            }

            _ = ComAbi.Release(loadedModule.Identity);
            _ = ComAbi.Release(loadedModule.Pointer);
            foreach (FunctionBreakpointDefinition definition in _definitions.Where(
                candidate => changed.Contains(candidate.Id) && candidate.BindingCount == 0))
            {
                await _notifyChanged(definition.ToInfo(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private static string? GetModulePath(nint module)
    {
        try
        {
            string path = CorDebugModulePath.Get(module);
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ReleaseModules()
    {
        foreach (CorDebugLoadedModule module in _modules.Values)
        {
            _ = ComAbi.Release(module.Identity);
            _ = ComAbi.Release(module.Pointer);
        }

        _modules.Clear();
    }
}
