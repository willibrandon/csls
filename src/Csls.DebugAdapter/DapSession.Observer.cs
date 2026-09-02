using Csls.Debugger.Contracts;

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

        if (_pendingConfigurationRequest is null || _pendingTargetRequest is null)
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
            await _writer.WriteEventAsync(
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
                cancellationToken).ConfigureAwait(false);
            ClearPendingTarget();
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnOutputAsync(
        DebugOutputCategory category,
        string output,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "output",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "category",
                        category == DebugOutputCategory.StandardOutput ? "stdout" : "stderr");
                    writer.WriteString("output", output);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnStoppedAsync(
        string reason,
        int? threadId,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        _ = generation;
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Stopped;
        try
        {
            await _writer.WriteEventAsync(
                "stopped",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", reason);
                    if (threadId is not null)
                    {
                        writer.WriteNumber("threadId", threadId.Value);
                    }

                    writer.WriteBoolean("allThreadsStopped", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnBreakpointChangedAsync(
        DebugSourceBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "breakpoint",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "changed");
                    writer.WritePropertyName("breakpoint");
                    WriteBreakpoint(writer, breakpoint);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnContinuedAsync(CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Running;
        try
        {
            await _writer.WriteEventAsync(
                "continued",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("allThreadsContinued", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

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
        }
    }

    /// <inheritdoc />
    public async ValueTask OnTerminatedAsync(CancellationToken cancellationToken)
    {
        bool endedWithoutClientRequest = _state == DapSessionState.Running;
        _state = DapSessionState.Terminated;
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
}
