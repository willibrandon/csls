using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Detaches and disposes a managed debuggee deterministically.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <inheritdoc />
    public void Detach()
    {
        _managedCallback.ThrowIfRuntimeFailed();
        if (Volatile.Read(ref _detached) != 0)
        {
            return;
        }

        nint corDebug = Volatile.Read(ref _corDebug);
        nint debugProcess = Volatile.Read(ref _debugProcess);
        if (debugProcess != 0)
        {
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(debugProcess).Detach(),
                "ICorDebugController.Detach");
        }

        _ = Interlocked.Exchange(ref _corDebug, 0);
        _ = Interlocked.Exchange(ref _debugProcess, 0);
        ReleaseRuntimeReferences(
            corDebug, debugProcess, runtimeAvailable: RuntimeFailure is null);
        Volatile.Write(ref _detached, 1);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _ownsRuntimeLease, 0) != 0)
            {
                CorDebugRuntimeActivationGate.Release();
            }
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (RuntimeFailure is CorDebugRuntimeException failure)
        {
            if (_ownsProcess)
            {
                await TerminateProcessAsync(_process, _unixExitMonitor, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await _actor.InvokeAsync(
                token =>
                {
                    _ = token;
                    AbandonFailedRuntime(failure);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
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
                await _actor.InvokeAsync(
                    token =>
                    {
                        _ = token;
                        if (RuntimeFailure is CorDebugRuntimeException runtimeFailure)
                        {
                            AbandonFailedRuntime(runtimeFailure);
                            return ValueTask.CompletedTask;
                        }

                        _ = PrepareForDetach();
                        Detach();
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                FailFunctionEvaluation(RuntimeFailure ?? (Exception)new OperationCanceledException(
                    "The debugger target ended during managed function evaluation."),
                    runtimeAvailable: RuntimeFailure is null);
                ClearFrameHandles();
                CancelStep(runtimeAvailable: RuntimeFailure is null);
                ReleaseRuntimeReferences(
                    corDebug, debugProcess, runtimeAvailable: RuntimeFailure is null);
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
