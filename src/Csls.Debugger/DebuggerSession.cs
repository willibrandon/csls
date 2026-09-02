using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one protocol-neutral debugger target and its ordered lifecycle.
/// </summary>
public sealed class DebuggerSession : IAsyncDisposable
{
    private readonly IDebuggerSessionObserver _observer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSessionActor _actor = new();
    private DebuggeeProcess? _debuggee;
    private Task? _debuggeeLifetime;
    private volatile DebugSessionState _state = DebugSessionState.Created;
    private int _disposed;

    /// <summary>
    /// Creates a debugger session connected to one protocol or control-plane observer.
    /// </summary>
    /// <param name="observer">The ordered target notification observer.</param>
    internal DebuggerSession(IDebuggerSessionObserver observer)
    {
        _observer = observer;
    }

    /// <summary>
    /// Gets the current protocol-neutral debugger state.
    /// </summary>
    public DebugSessionState State => _state;

    /// <summary>
    /// Launches and owns a target without activating managed runtime debugging.
    /// </summary>
    /// <param name="options">The validated target launch options.</param>
    /// <param name="cancellationToken">Cancels launch and initial notification.</param>
    /// <returns>A task that completes after the target-start notification is accepted.</returns>
    public Task LaunchWithoutDebuggingAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        return _actor.InvokeAsync(
            token => LaunchWithoutDebuggingCoreAsync(options, token),
            cancellationToken);
    }

    /// <summary>
    /// Terminates the owned target process and all of its descendants.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for process termination.</param>
    /// <returns>A task that completes after final target notifications are delivered.</returns>
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _actor.InvokeAsync(TerminateCoreAsync, cancellationToken).ConfigureAwait(false);
        Task? debuggeeLifetime = _debuggeeLifetime;
        if (debuggeeLifetime is not null)
        {
            await debuggeeLifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _actor.InvokeAsync(TerminateCoreAsync, CancellationToken.None).ConfigureAwait(false);
        if (_debuggeeLifetime is not null)
        {
            await _debuggeeLifetime.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_debuggee is not null)
        {
            await _debuggee.DisposeAsync().ConfigureAwait(false);
            _debuggee = null;
            _debuggeeLifetime = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _actor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async ValueTask LaunchWithoutDebuggingCoreAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Created)
        {
            throw new InvalidOperationException(
                $"A target cannot be launched while the debugger session is {_state}.");
        }

        _state = DebugSessionState.Starting;
        try
        {
            _debuggee = DebuggeeProcess.Start(options);
        }
        catch
        {
            _state = DebugSessionState.Created;

            throw;
        }

        try
        {
            _state = DebugSessionState.Running;
            await _observer.OnProcessStartedAsync(
                _debuggee.Name,
                _debuggee.Id,
                cancellationToken).ConfigureAwait(false);
            _debuggeeLifetime = ObserveDebuggeeAsync(_debuggee, _lifetime.Token);
        }
        catch
        {
            _state = DebugSessionState.Faulted;
            await _debuggee.DisposeAsync().ConfigureAwait(false);
            _debuggee = null;

            throw;
        }
    }

    private async ValueTask TerminateCoreAsync(CancellationToken cancellationToken)
    {
        DebuggeeProcess? debuggee = _debuggee;
        if (debuggee is null)
        {
            return;
        }

        if (_state is not DebugSessionState.Terminated and not DebugSessionState.Faulted)
        {
            _state = DebugSessionState.Terminating;
        }

        await debuggee.TerminateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveDebuggeeAsync(
        DebuggeeProcess debuggee,
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
