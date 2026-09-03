using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Configures managed exception stops and exposes current exception details.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces the managed exception stages that should stop execution.
    /// </summary>
    /// <param name="breakModes">The complete replacement exception-stage set.</param>
    /// <param name="cancellationToken">Cancels queueing exception configuration.</param>
    /// <returns>A task that completes after configuration is applied.</returns>
    public Task SetExceptionBreakModesAsync(
        IReadOnlyCollection<DebugExceptionBreakMode> breakModes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(breakModes);
        return _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state is not DebugSessionState.Created and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Exception breakpoints cannot be changed while the debugger session is {_state}.");
                }

                _exceptionBreakModes.Clear();
                foreach (DebugExceptionBreakMode breakMode in breakModes)
                {
                    _ = _exceptionBreakModes.Add(breakMode);
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets the managed exception responsible for the current stop.
    /// </summary>
    /// <param name="threadId">The managed thread identifier from the exception stop.</param>
    /// <param name="cancellationToken">Cancels queueing exception inspection.</param>
    /// <returns>The current managed exception details.</returns>
    public async Task<DebugExceptionInfo> GetExceptionInfoAsync(
        int threadId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugExceptionInfo? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped || _currentException is null ||
                    _currentExceptionThreadId != threadId)
                {
                    throw new InvalidOperationException(
                        "Exception information is available only at a managed exception stop.");
                }

                result = _currentException;
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private async ValueTask<bool> HandleRuntimeExceptionCoreAsync(
        int threadId,
        nint thread,
        DebugExceptionStage stage,
        CancellationToken cancellationToken)
    {
        DebugExceptionBreakMode breakMode = stage switch
        {
            DebugExceptionStage.Thrown => DebugExceptionBreakMode.Thrown,
            DebugExceptionStage.UserUnhandled => DebugExceptionBreakMode.UserUnhandled,
            DebugExceptionStage.Unhandled => DebugExceptionBreakMode.Unhandled,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        if (!_exceptionBreakModes.Contains(breakMode))
        {
            return false;
        }

        string exceptionId = CorDebugExceptionInspector.GetTypeName(thread);
        var exception = new DebugExceptionInfo(
            exceptionId,
            DescribeException(exceptionId, breakMode),
            breakMode);
        if (_state == DebugSessionState.Starting)
        {
            _pendingStop = new PendingDebugStop("exception", threadId, exception);
            return true;
        }

        if (_state != DebugSessionState.Running)
        {
            return false;
        }

        if (_debuggee is CorDebugDebuggee managedDebuggee)
        {
            managedDebuggee.CancelStep();
        }

        _currentException = exception;
        _currentExceptionThreadId = threadId;
        await EnterStoppedStateAsync("exception", threadId, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static string DescribeException(
        string exceptionId,
        DebugExceptionBreakMode breakMode) => breakMode switch
        {
            DebugExceptionBreakMode.Thrown => $"Exception thrown: '{exceptionId}'.",
            DebugExceptionBreakMode.UserUnhandled =>
                $"Exception '{exceptionId}' was not handled in user code.",
            DebugExceptionBreakMode.Unhandled => $"Unhandled exception: '{exceptionId}'.",
            _ => throw new ArgumentOutOfRangeException(nameof(breakMode))
        };
}
