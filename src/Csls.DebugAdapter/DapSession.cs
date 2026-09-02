using Csls.DebugAdapter.Protocol;
using Csls.Debugger;
using Csls.Debugger.Contracts;
using System.ComponentModel;

namespace Csls.DebugAdapter;

/// <summary>
/// Translates one Debug Adapter Protocol connection to a protocol-neutral debugger session.
/// </summary>
internal sealed class DapSession : IDebuggerSessionObserver, IAsyncDisposable
{
    private readonly DapMessageReader _reader;
    private readonly DapMessageWriter _writer;
    private readonly Func<string, Task> _writeErrorAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSession _engineSession;
    private DapSessionState _state = DapSessionState.Created;
    private Request? _pendingLaunchRequest;
    private Request? _pendingConfigurationRequest;
    private DapLaunchConfiguration? _pendingLaunch;
    private int _protocolClosed;

    /// <summary>
    /// Creates a DAP session over explicit protocol and diagnostic streams.
    /// </summary>
    /// <param name="input">The protocol input stream.</param>
    /// <param name="output">The protocol output stream.</param>
    /// <param name="error">The diagnostics-only text stream.</param>
    internal DapSession(Stream input, Stream output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _reader = new DapMessageReader(input);
        _writer = new DapMessageWriter(output);
        _writeErrorAsync = error.WriteLineAsync;
        _engineSession = DebuggerEngine.CreateSession(this);
    }

    /// <summary>
    /// Processes requests until disconnect, end of input, cancellation, or protocol failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels the complete adapter session.</param>
    /// <returns>Zero for a normal session or one for a terminal protocol failure.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            while (_state is not DapSessionState.Terminated and not DapSessionState.Faulted)
            {
                Request? request = await _reader
                    .ReadRequestAsync(linked.Token)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                await HandleRequestAsync(request, linked.Token).ConfigureAwait(false);
            }

            return _state == DapSessionState.Faulted ? 1 : 0;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return 0;
        }
        catch (InvalidDataException exception)
        {
            _state = DapSessionState.Faulted;
            await _writeErrorAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Volatile.Write(ref _protocolClosed, 1);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _engineSession.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

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

        if (_pendingConfigurationRequest is null || _pendingLaunchRequest is null)
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
                _pendingLaunchRequest,
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
                    writer.WriteString("startMethod", "launch");
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
            ClearPendingLaunch();
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

    private async ValueTask HandleRequestAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "initialize":
                await InitializeAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "launch":
                await PrepareLaunchAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "configurationDone":
                await CompleteLaunchAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "threads":
                await WriteThreadsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "disconnect":
                await DisconnectAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "cancel":
                await _writer.WriteResponseAsync(
                    request,
                    success: true,
                    message: null,
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                await _writer.WriteResponseAsync(
                    request,
                    success: false,
                    $"The request '{request.Command}' is not supported by this debugger capability set.",
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask InitializeAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Created)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Initialized;
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: static writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("supportsConfigurationDoneRequest", true);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PrepareLaunchAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Initialized)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _pendingLaunch = DapLaunchOptionsParser.Parse(request.Arguments);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _pendingLaunchRequest = request;
        _state = DapSessionState.Configuring;
        await _writer.WriteEventAsync(
            "initialized",
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteLaunchAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Configuring ||
            _pendingLaunchRequest is null ||
            _pendingLaunch is null)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Starting;
        _pendingConfigurationRequest = request;
        try
        {
            if (_pendingLaunch.NoDebug)
            {
                await _engineSession
                    .LaunchWithoutDebuggingAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _engineSession
                    .LaunchManagedAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            Win32Exception)
        {
            _state = DapSessionState.Initialized;
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                _pendingLaunchRequest,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            ClearPendingLaunch();
        }
    }

    private ValueTask WriteThreadsAsync(
        Request request,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: _state == DapSessionState.Running,
            _state == DapSessionState.Running
                ? null
                : $"The request '{request.Command}' is invalid while the session is {_state}.",
            static writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("threads");
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);

    private async ValueTask DisconnectAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        _state = DapSessionState.Terminating;
        await _engineSession.TerminateAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
        _state = DapSessionState.Terminated;
    }

    private ValueTask WriteStateFailureAsync(
        Request request,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            $"The request '{request.Command}' is invalid while the session is {_state}.",
            writeBody: null,
            cancellationToken);

    private void ClearPendingLaunch()
    {
        _pendingLaunchRequest = null;
        _pendingConfigurationRequest = null;
        _pendingLaunch = null;
    }

    private bool IsProtocolClosed => Volatile.Read(ref _protocolClosed) != 0;

    private bool IsExpectedClosedTransportException(Exception exception) =>
        IsProtocolClosed && exception is IOException or ObjectDisposedException or OperationCanceledException;
}
