using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Rebinds managed instruction breakpoints after runtime method bodies change.
/// </summary>
internal sealed partial class InstructionBreakpointManager
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
        if (!_modules.TryGetValue(moduleIdentity, out InstructionBreakpointModule? module))
        {
            throw new InvalidOperationException(
                "The Hot Reload module is not registered for instruction breakpoints.");
        }

        IReadOnlyDictionary<int, DebugInstructionBreakpointInfo> previous = _definitions
            .ToDictionary(static definition => definition.Id, static definition => definition.ToInfo());
        foreach ((nint identity, InstructionBreakpointBinding binding) in _bindings.ToArray())
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
        foreach (InstructionBreakpointDefinition definition in _definitions)
        {
            DebugInstructionBreakpointInfo current = definition.ToInfo();
            if (previous.GetValueOrDefault(definition.Id) != current)
            {
                await _notifyChanged(current, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
