namespace Csls.Debugger;

/// <summary>
/// Detaches and disposes a managed debuggee deterministically.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <inheritdoc />
    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                DetachRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _detached) == 0)
        {
            if (_ownsProcess)
            {
                await TerminateProcessAsync(_process, _unixExitMonitor, CancellationToken.None)
                    .ConfigureAwait(false);
                await _managedCallback.WaitForExitProcessAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await DetachAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                ClearFrameHandles();
                CancelStep();
                ReleaseRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        _registration.Dispose();
        _managedCallback.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        if (_standardStreams is not null)
        {
            await _standardStreams.DisposeAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }
}
