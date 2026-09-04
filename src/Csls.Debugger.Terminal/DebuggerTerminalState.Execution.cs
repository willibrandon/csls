using Csls.Debugger.Contracts;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Applies interactive execution operations without blocking later terminal input.
/// </summary>
internal sealed partial class DebuggerTerminalState
{
    /// <summary>
    /// Executes a command selected from the terminal command palette.
    /// </summary>
    /// <param name="command">The selected debugger command.</param>
    /// <returns>A task that completes after the command updates terminal state.</returns>
    internal async Task ExecuteCommandAsync(DebuggerTerminalCommand command)
    {
        try
        {
            await (command switch
            {
                DebuggerTerminalCommand.ClearWatches => ClearWatchesAsync(),
                DebuggerTerminalCommand.Continue => ContinueAsync(),
                DebuggerTerminalCommand.Pause => PauseAsync(),
                DebuggerTerminalCommand.StepOver => StepAsync(DebugStepKind.Over),
                DebuggerTerminalCommand.StepInto => StepAsync(DebugStepKind.Into),
                DebuggerTerminalCommand.StepOut => StepAsync(DebugStepKind.Out),
                DebuggerTerminalCommand.ToggleBreakpoint => ToggleSourceBreakpointAsync(),
                DebuggerTerminalCommand.Restart => RestartAsync(),
                DebuggerTerminalCommand.Terminate => TerminateAsync(),
                DebuggerTerminalCommand.Detach => DetachAsync(),
                DebuggerTerminalCommand.AddWatch => throw new InvalidOperationException(
                    "The add-watch command requires an expression prompt."),
                _ => throw new InvalidOperationException($"Unknown terminal command {command}.")
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            ObjectDisposedException or
            StreamJsonRpc.RemoteInvocationException)
        {
            await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                PublishViewError(exception.Message);
            }
            finally
            {
                _ = _mutationGate.Release();
            }
        }
    }

    /// <summary>
    /// Adds and evaluates one side-effect-free watch in the selected frame.
    /// </summary>
    /// <param name="expression">The expression entered by the developer.</param>
    /// <returns>A task that completes after all watches are refreshed.</returns>
    internal async Task AddWatchAsync(string expression)
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RequireState(DebugSessionState.Stopped, "add a watch") ||
                _selectedFrame is null)
            {
                return;
            }

            await _auxiliary.AddWatchAsync(
                _selectedFrame.Id,
                expression,
                _cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Watching {expression.Trim()}.";
            PublishViewSnapshot();
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            StreamJsonRpc.RemoteInvocationException)
        {
            StatusMessage = exception.Message;
            PublishViewSnapshot();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

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
            PublishViewSnapshot();
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
            PublishViewSnapshot();
            StartRunObservation();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Restarts the activated target and observes its next managed stop.
    /// </summary>
    /// <returns>A task that completes after replacement target activation.</returns>
    internal async Task RestartAsync()
    {
        await StopRunObservationAsync().ConfigureAwait(false);
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot = await _client.RestartAsync(_cancellationToken).ConfigureAwait(false);
            StatusMessage = "Restarted target.";
            ClearInspection();
            PublishViewSnapshot();
            StartRunObservation();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Terminates the activated target and clears generation-bound state.
    /// </summary>
    /// <returns>A task that completes after target termination.</returns>
    internal Task TerminateAsync() => EndTargetAsync(terminate: true);

    /// <summary>
    /// Detaches from the activated target and clears generation-bound state.
    /// </summary>
    /// <returns>A task that completes after debugger detachment.</returns>
    internal Task DetachAsync() => EndTargetAsync(terminate: false);

    private async Task ClearWatchesAsync()
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            _auxiliary.ClearWatches();
            StatusMessage = "Cleared watches.";
            PublishViewSnapshot();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    private async Task EndTargetAsync(bool terminate)
    {
        await StopRunObservationAsync().ConfigureAwait(false);
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot = terminate
                ? await _client.TerminateAsync(_cancellationToken).ConfigureAwait(false)
                : await _client.DetachAsync(_cancellationToken).ConfigureAwait(false);
            StatusMessage = terminate ? "Terminated target." : "Detached from target.";
            ClearInspection();
            PublishViewSnapshot();
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
        PublishViewSnapshot();
        return false;
    }
}
