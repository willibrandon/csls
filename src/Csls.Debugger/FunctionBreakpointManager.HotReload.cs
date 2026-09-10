using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Rebinds managed function breakpoints after runtime method bodies change.
/// </summary>
internal sealed partial class FunctionBreakpointManager
{
    /// <summary>
    /// Recreates every affected runtime binding against the current module generation.
    /// </summary>
    /// <param name="moduleIdentity">The retained canonical module identity.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task completed after rebinding and notifications finish.</returns>
    internal async ValueTask RebindHotReloadModuleAsync(
        nint moduleIdentity,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(moduleIdentity);
        if (!_modules.TryGetValue(moduleIdentity, out CorDebugLoadedModule? module))
        {
            throw new InvalidOperationException(
                "The Hot Reload module is not registered for function breakpoints.");
        }

        IReadOnlyDictionary<int, DebugFunctionBreakpointInfo> previous = _definitions
            .ToDictionary(static definition => definition.Id, static definition => definition.ToInfo());
        foreach ((nint identity, FunctionBreakpointBinding binding) in _bindings.ToArray())
        {
            if (binding.ModuleIdentity != moduleIdentity)
            {
                continue;
            }

            ReleaseBinding(binding);
            _ = _bindings.Remove(identity);
            binding.Definition.BindingCount--;
        }

        await BindModuleAsync(module, notifyChanges: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (FunctionBreakpointDefinition definition in _definitions)
        {
            DebugFunctionBreakpointInfo current = definition.ToInfo();
            if (previous.GetValueOrDefault(definition.Id) != current)
            {
                await _notifyChanged(current, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
