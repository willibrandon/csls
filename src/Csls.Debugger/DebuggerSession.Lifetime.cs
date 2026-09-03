using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Observes and deterministically releases one debugger target lifetime.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DisposeDebuggeeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _debuggeeObservationCancellation?.Dispose();
        await _actor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task DisposeDebuggeeAsync()
    {
        if (_debuggee is not null &&
            _state is not DebugSessionState.Terminated and not DebugSessionState.Faulted)
        {
            if (_debuggee.OwnsProcess)
            {
                await _actor.InvokeAsync(TerminateCoreAsync, CancellationToken.None)
                    .ConfigureAwait(false);
                if (_debuggeeLifetime is not null)
                {
                    await _debuggeeLifetime.WaitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await DisposeAttachedDebuggeeAsync().ConfigureAwait(false);
            }
        }

        if (_debuggee is not null)
        {
            await _actor.InvokeAsync(
                token =>
                {
                    _ = token;
                    _sourceBreakpoints.Dispose();
                    _functionBreakpoints.Dispose();
                    _instructionBreakpoints.Dispose();
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
            await _debuggee.DisposeAsync().ConfigureAwait(false);
            _debuggee = null;
            _debuggeeLifetime = null;
        }
        else
        {
            _sourceBreakpoints.Dispose();
            _functionBreakpoints.Dispose();
            _instructionBreakpoints.Dispose();
        }
    }

    private async Task DisposeAttachedDebuggeeAsync()
    {
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                _state = DebugSessionState.Terminating;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        await _debuggee!.DetachAsync(CancellationToken.None).ConfigureAwait(false);
        await StopObservingDebuggeeAsync().ConfigureAwait(false);
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                _state = DebugSessionState.Terminated;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ObserveDebuggeeAsync(
        IDebuggeeProcess debuggee,
        CancellationToken cancellationToken)
    {
        Task standardOutput = debuggee.CopyStandardOutputAsync(
            (value, token) => _observer.OnOutputAsync(
                DebugOutputCategory.StandardOutput,
                value,
                token),
            cancellationToken);
        Task standardError = debuggee.CopyStandardErrorAsync(
            (value, token) => _observer.OnOutputAsync(
                DebugOutputCategory.StandardError,
                value,
                token),
            cancellationToken);
        int exitCode = await debuggee.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        await _actor.InvokeAsync(
            async token =>
            {
                await _observer.OnExitedAsync(exitCode, token).ConfigureAwait(false);
                _state = DebugSessionState.Terminated;
                await _observer.OnTerminatedAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
