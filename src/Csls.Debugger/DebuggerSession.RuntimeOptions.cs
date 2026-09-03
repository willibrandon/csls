using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Configures runtime behavior before a debugger target is activated.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces managed runtime options before target activation.
    /// </summary>
    /// <param name="justMyCode">Whether source stepping excludes non-user managed code.</param>
    /// <param name="enableStepFiltering">Whether stepping skips properties and operators.</param>
    /// <param name="cancellationToken">Cancels queueing the configuration.</param>
    /// <returns>A task that completes after the options are installed.</returns>
    public Task ConfigureRuntimeOptionsAsync(
        bool justMyCode,
        bool enableStepFiltering,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Created)
                {
                    throw new InvalidOperationException(
                        $"Runtime options cannot change while the debugger session is {_state}.");
                }

                _sourceBreakpoints.SetRuntimeOptions(
                    suppressJitOptimizations: false,
                    justMyCode: justMyCode,
                    enableStepFiltering: enableStepFiltering);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }
}
