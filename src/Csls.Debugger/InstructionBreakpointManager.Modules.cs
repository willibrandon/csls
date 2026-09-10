using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Tracks modules used for managed-IL breakpoint rebinding.
/// </summary>
internal sealed partial class InstructionBreakpointManager
{
    /// <summary>
    /// Retains a newly loaded module and binds matching definitions.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="moduleId">The stable source-manager module identifier.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notification.</param>
    /// <returns>A task completed after matching breakpoints bind.</returns>
    internal async ValueTask LoadModuleAsync(
        nint module,
        int? moduleId,
        CancellationToken cancellationToken)
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
        var loadedModule = new InstructionBreakpointModule
        {
            Id = moduleId,
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
    /// Removes bindings and ownership for one unloading module.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notification.</param>
    /// <returns>A task completed after affected breakpoint changes publish.</returns>
    internal async ValueTask UnloadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(module);
        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            if (!_modules.Remove(identity, out InstructionBreakpointModule? loadedModule))
            {
                return;
            }

            var affected = new HashSet<InstructionBreakpointDefinition>();
            foreach ((nint breakpointIdentity, InstructionBreakpointBinding binding) in
                _bindings.ToArray())
            {
                if (binding.ModuleIdentity != identity)
                {
                    continue;
                }

                ReleaseBinding(binding);
                _ = _bindings.Remove(breakpointIdentity);
                binding.Definition.BindingCount--;
                _ = affected.Add(binding.Definition);
            }

            _ = ComAbi.Release(loadedModule.Identity);
            _ = ComAbi.Release(loadedModule.Pointer);
            foreach (InstructionBreakpointDefinition definition in affected.Where(
                static definition => definition.BindingCount == 0))
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
        foreach (InstructionBreakpointModule module in _modules.Values)
        {
            _ = ComAbi.Release(module.Identity);
            _ = ComAbi.Release(module.Pointer);
        }

        _modules.Clear();
    }
}
