using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Projects debugger engine notifications into the private RPC session snapshot.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public ValueTask OnProcessStartedAsync(
        string name,
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = DebugSessionState.Running,
            ProcessName = name,
            ProcessId = processId
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnOutputAsync(
        DebugOutputCategory category,
        string output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.Add(category, output);
        NotifyResourceChanged(DebuggerResourceChangeKind.Output);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnStoppedAsync(
        string reason,
        int? threadId,
        DebugStopGeneration generation,
        DebugExceptionInfo? exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = DebugSessionState.Stopped,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopReason = reason,
            StoppedThreadId = threadId,
            StopGeneration = generation.Value,
            Exception = exception
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnBreakpointChangedAsync(
        DebugSourceBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        _ = breakpoint;
        cancellationToken.ThrowIfCancellationRequested();
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnContinuedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = DebugSessionState.Running,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopGeneration = current.StopGeneration
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnExitedAsync(int exitCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = current.State,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopGeneration = current.StopGeneration,
            ExitCode = exitCode
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnTerminatedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = _session.State == DebugSessionState.Faulted
                ? DebugSessionState.Faulted
                : DebugSessionState.Terminated,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopGeneration = current.StopGeneration,
            ExitCode = current.ExitCode
        });
        return ValueTask.CompletedTask;
    }
}
