using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Retires rejected runtime bindings and publishes their logical breakpoint state.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private const int UnableToSetBreakpointHResult = unchecked((int)0x80131345);
    private const string RuntimeBindingFailureMessage =
        "The runtime could not map this source location to an executable instruction. " +
        "Compiler optimizations can remove breakpoint locations; choose another statement or rebuild the target in Debug.";

    /// <summary>
    /// Deactivates a source binding that CoreCLR could not map after compiling its method.
    /// </summary>
    /// <param name="breakpoint">The borrowed ICorDebugBreakpoint pointer reported by CoreCLR.</param>
    /// <param name="cancellationToken">Cancels publication of the updated breakpoint state.</param>
    /// <returns>A task completed after the failed binding is retired and its state is published.</returns>
    internal async ValueTask RejectBindingAsync(nint breakpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(breakpoint);
        nint identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
        try
        {
            if (!_bindings.TryGetValue(identity, out SourceBreakpointBinding? binding))
            {
                return;
            }

            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0),
                "ICorDebugBreakpoint.Activate");
            _ = _bindings.Remove(identity);
            _ = ComAbi.Release(binding.Identity);
            _ = ComAbi.Release(binding.Breakpoint);
            if (_modules.TryGetValue(binding.ModuleIdentity, out CorDebugLoadedModule? module))
            {
                await ReportBindingFailureAsync(module, binding.Definition, notifyChanges: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private async ValueTask ReportBindingFailureAsync(
        CorDebugLoadedModule module,
        SourceBreakpointDefinition definition,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        string? previousMessage = definition.ToInfo().Message;
        definition.BindingFailures[module.Id] = RuntimeBindingFailureMessage;
        if (_bindings.Values.Any(candidate => candidate.BreakpointId == definition.Id))
        {
            return;
        }

        bool changed = definition.ResolvedLine is not null ||
            !string.Equals(previousMessage, definition.ToInfo().Message, StringComparison.Ordinal);
        definition.ResolvedLine = null;
        definition.ResolvedColumn = null;
        if (notifyChanges && changed)
        {
            await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
        }
    }
}
