using Csls.DebugAdapter.Protocol;
using Csls.Debugger;
using Csls.Debugger.Contracts;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Translates one Debug Adapter Protocol connection to a protocol-neutral debugger session.
/// </summary>
internal sealed partial class DapSession : IDebuggerSessionObserver, IAsyncDisposable
{
    private readonly DapMessageReader _reader;
    private readonly DapMessageWriter _writer;
    private readonly Func<string, Task> _writeErrorAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSession _engineSession;
    private DapSessionState _state = DapSessionState.Created;
    private Request? _pendingTargetRequest;
    private Request? _pendingConfigurationRequest;
    private DapLaunchConfiguration? _pendingLaunch;
    private int? _pendingAttachProcessId;
    private string _startMethod = "launch";
    private bool _terminateDebuggeeByDefault = true;
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
            case "attach":
                await PrepareAttachAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "configurationDone":
                await CompleteTargetStartAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "setBreakpoints":
                await SetBreakpointsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "threads":
                await WriteThreadsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "modules":
                await WriteModulesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "pause":
                await PauseAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "continue":
                await ContinueAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "next":
                await StepAsync(request, DebugStepKind.Over, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepIn":
                await StepAsync(request, DebugStepKind.Into, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepOut":
                await StepAsync(request, DebugStepKind.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stackTrace":
                await WriteStackTraceAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "scopes":
                await WriteScopesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "variables":
                await WriteVariablesAsync(request, cancellationToken).ConfigureAwait(false);
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
                writer.WriteBoolean("supportsModulesRequest", true);
                writer.WriteBoolean("supportsVariablePaging", true);
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

        _pendingTargetRequest = request;
        _pendingAttachProcessId = null;
        _startMethod = "launch";
        _terminateDebuggeeByDefault = true;
        _state = DapSessionState.Configuring;
        await _writer.WriteEventAsync(
            "initialized",
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteTargetStartAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Configuring ||
            _pendingTargetRequest is null ||
            (_pendingLaunch is null) == (_pendingAttachProcessId is null))
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Starting;
        _pendingConfigurationRequest = request;
        try
        {
            if (_pendingAttachProcessId is int processId)
            {
                await _engineSession
                    .AttachManagedAsync(processId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (_pendingLaunch!.NoDebug)
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
            exception is ArgumentException or InvalidOperationException or
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
                _pendingTargetRequest,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            ClearPendingTarget();
        }
    }

    private static int GetOptionalNonNegativeInteger(
        JsonElement arguments,
        string propertyName,
        string requestName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (!value.TryGetInt32(out int result) || result < 0)
        {
            throw new ArgumentException(
                $"The {requestName} {propertyName} value must be a non-negative integer.");
        }

        return result;
    }

    private static int GetRequiredInteger(
        JsonElement arguments,
        string propertyName,
        string requestName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out JsonElement value) ||
            !value.TryGetInt32(out int result))
        {
            throw new ArgumentException(
                $"The {requestName} request requires an integer {propertyName}.");
        }

        return result;
    }

    private ValueTask WriteRequestFailureAsync(
        Request request,
        string message,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            message,
            writeBody: null,
            cancellationToken);

    private ValueTask WriteStateFailureAsync(
        Request request,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            $"The request '{request.Command}' is invalid while the session is {_state}.",
            writeBody: null,
            cancellationToken);

    private void ClearPendingTarget()
    {
        _pendingTargetRequest = null;
        _pendingConfigurationRequest = null;
        _pendingLaunch = null;
        _pendingAttachProcessId = null;
    }

    private bool IsProtocolClosed => Volatile.Read(ref _protocolClosed) != 0;

    private bool IsExpectedClosedTransportException(Exception exception) =>
        IsProtocolClosed && exception is IOException or ObjectDisposedException or OperationCanceledException;
}
