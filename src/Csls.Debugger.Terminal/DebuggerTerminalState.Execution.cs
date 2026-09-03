using Csls.Debugger.Contracts;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Applies interactive execution operations without blocking later terminal input.
/// </summary>
internal sealed partial class DebuggerTerminalState
{
    /// <summary>
    /// Continues execution and observes the next stop in the background.
    /// </summary>
    /// <returns>A task that completes after the target resumes.</returns>
    internal async Task ContinueAsync()
    {
        await StopRunObservationAsync().ConfigureAwait(false);
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RequireState(DebugSessionState.Stopped, "continue"))
            {
                return;
            }

            Snapshot = await _client.ContinueAsync(_cancellationToken).ConfigureAwait(false);
            StatusMessage = null;
            ClearInspection();
            _app?.Invalidate();
            StartRunObservation();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Pauses a running target and reloads its managed state.
    /// </summary>
    /// <returns>A task that completes after refreshed state is available.</returns>
    internal async Task PauseAsync()
    {
        await StopRunObservationAsync().ConfigureAwait(false);
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot = await _client.GetSessionAsync(_cancellationToken).ConfigureAwait(false);
            if (Snapshot.State == DebugSessionState.Stopped)
            {
                await LoadStoppedStateAsync(_cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!RequireState(DebugSessionState.Running, "pause"))
            {
                return;
            }

            Snapshot = await _client.PauseAsync(_cancellationToken).ConfigureAwait(false);
            StatusMessage = null;
            await LoadStoppedStateAsync(_cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Performs one source-level step and observes the next stop in the background.
    /// </summary>
    /// <param name="kind">The source-level step kind.</param>
    /// <returns>A task that completes after the target resumes.</returns>
    internal async Task StepAsync(DebugStepKind kind)
    {
        await StopRunObservationAsync().ConfigureAwait(false);
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RequireState(DebugSessionState.Stopped, "step"))
            {
                return;
            }

            int threadId = Snapshot.StoppedThreadId
                ?? throw new InvalidOperationException("The stopped thread is unavailable.");
            Snapshot = await _client.StepAsync(
                new DebugStepRequest(threadId, kind),
                _cancellationToken).ConfigureAwait(false);
            StatusMessage = null;
            ClearInspection();
            _app?.Invalidate();
            StartRunObservation();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    private bool RequireState(DebugSessionState expected, string operation)
    {
        if (Snapshot.State == expected)
        {
            return true;
        }

        StatusMessage = $"Cannot {operation} while the target is {Snapshot.State}.";
        _app?.Invalidate();
        return false;
    }
}
