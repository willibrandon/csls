using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Publishes protocol-neutral debugger events as DAP notifications.
/// </summary>
internal sealed partial class DapSession
{
    /// <inheritdoc />
    public async ValueTask OnProcessStartedAsync(
        string name,
        int processId,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        if (_isRestarting)
        {
            await CompleteRestartAsync(name, processId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_pendingConfigurationRequest is null || _pendingTargetRequest is null ||
            _pendingTargetArguments is null)
        {
            throw new InvalidOperationException(
                "The engine reported a process before DAP launch configuration completed.");
        }

        try
        {
            _state = DapSessionState.Running;
            await _writer.WriteResponseAsync(
                _pendingConfigurationRequest,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                _pendingTargetRequest,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await WriteProcessEventAsync(name, processId, cancellationToken)
                .ConfigureAwait(false);
            _activeTargetArguments = _pendingTargetArguments;
            ClearPendingTarget();
        }
        catch (Exception transportException) when (
            IsExpectedClosedTransportException(transportException))
        {
            return;
        }
    }

    /// <inheritdoc />
    public async ValueTask OnStoppedAsync(
        string reason,
        int? threadId,
        DebugStopGeneration generation,
        DebugExceptionInfo? exception,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Stopped;
        _stoppedThreadId = threadId ?? _stoppedThreadId;
        if (_deferGotoStoppedEvent && string.Equals(reason, "goto", StringComparison.Ordinal))
        {
            _deferredStop = (reason, threadId, generation, exception);
            return;
        }

        try
        {
            await WriteStoppedEventAsync(reason, threadId, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception transportException) when (
            IsExpectedClosedTransportException(transportException))
        {
            return;
        }
    }

    private ValueTask WriteStoppedEventAsync(
        string reason,
        int? threadId,
        DebugExceptionInfo? exception,
        CancellationToken cancellationToken) => _writer.WriteEventAsync(
            "stopped",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("reason", reason);
                if (exception is not null)
                {
                    writer.WriteString("description", exception.Description);
                    writer.WriteString("text", exception.ExceptionId);
                }

                if (threadId is not null)
                {
                    writer.WriteNumber("threadId", threadId.Value);
                }

                writer.WriteBoolean("allThreadsStopped", true);
                writer.WriteEndObject();
            },
            cancellationToken);

    /// <inheritdoc />
    public async ValueTask OnExitedAsync(int exitCode, CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "exited",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("exitCode", exitCode);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }
    }

    /// <inheritdoc />
    public async ValueTask OnTerminatedAsync(CancellationToken cancellationToken)
    {
        if (_isRestarting)
        {
            return;
        }

        bool runtimeFailed = _engineSession.State == DebugSessionState.Faulted;
        bool endedWithoutClientRequest = _state == DapSessionState.Running || runtimeFailed;
        _state = runtimeFailed
            ? DapSessionState.Faulted
            : DapSessionState.Terminated;
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "terminated",
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }

        if (endedWithoutClientRequest)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CompleteRestartAsync(
        string name,
        int processId,
        CancellationToken cancellationToken)
    {
        Request request = _restartRequest ?? throw new InvalidOperationException(
            "The engine reported a restarted process without a DAP restart request.");
        JsonElement arguments = _restartTargetArguments ?? throw new InvalidOperationException(
            "The engine reported a restarted process without target arguments.");
        _state = DapSessionState.Running;
        _activeTargetArguments = arguments;
        _restartRequest = null;
        _restartTargetArguments = null;
        _isRestarting = false;
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
        await WriteProcessEventAsync(name, processId, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteProcessEventAsync(
        string name,
        int processId,
        CancellationToken cancellationToken) => _writer.WriteEventAsync(
            "process",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteNumber("systemProcessId", processId);
                writer.WriteBoolean("isLocalProcess", true);
                writer.WriteString("startMethod", _startMethod);
                writer.WriteEndObject();
            },
            cancellationToken);
}
