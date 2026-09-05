using Csls.DebugAdapter.Protocol;
using Csls.Debugger;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Translates one Debug Adapter Protocol connection to a protocol-neutral debugger session.
/// </summary>
internal sealed partial class DapSession : IDebuggerSessionObserver, IAsyncDisposable
{
    private readonly DapMessageReader _reader;
    private readonly DapMessageWriter _writer;
    private readonly DapRequestQueue _pendingRequests = new();
    private readonly Func<string, Task> _writeErrorAsync;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource _targetCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _stopEventGate = new(1, 1);
    private readonly DebuggerSession _engineSession;
    private DapSessionState _state = DapSessionState.Created;
    private Request? _pendingTargetRequest;
    private Request? _pendingConfigurationRequest;
    private DapLaunchConfiguration? _pendingLaunch;
    private DapAttachConfiguration? _pendingAttach;
    private JsonElement? _pendingTargetArguments;
    private JsonElement? _activeTargetArguments;
    private JsonElement? _restartTargetArguments;
    private Request? _restartRequest;
    private bool _isRestarting;
    private string _startMethod = "launch";
    private bool _terminateDebuggeeByDefault = true;
    private bool _clientLinesStartAtOne = true;
    private bool _clientColumnsStartAtOne = true;
    private bool _clientSupportsVariablePaging;
    private bool _clientSupportsInvalidatedEvent;
    private bool _targetExited;
    private int? _stoppedThreadId;
    private string? _deferredStoppedReason;
    private (string Reason, int? ThreadId, DebugStopGeneration Generation,
        DebugExceptionInfo? Exception)? _deferredStop;
    private Task? _cancelableRequest;
    private CancellationTokenSource? _cancelableRequestCancellation;
    private int _cancelableRequestSequence;
    private int _protocolClosed;

    /// <summary>
    /// Creates a DAP session over explicit protocol and diagnostic streams.
    /// </summary>
    /// <param name="input">The protocol input stream.</param>
    /// <param name="output">The protocol output stream.</param>
    /// <param name="error">The diagnostics-only text stream.</param>
    /// <param name="cancellationToken">Cancels the complete protocol connection.</param>
    internal DapSession(Stream input, Stream output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reader = new DapMessageReader(input);
        _writer = new DapMessageWriter(output, _lifetime.Token);
        _writeErrorAsync = error.WriteLineAsync;
        _engineSession = DebuggerEngine.CreateSession(this);
    }

    /// <summary>
    /// Processes requests until disconnect, end of input, cancellation, or protocol failure.
    /// </summary>
    /// <returns>Zero for a normal session or one for a terminal protocol failure.</returns>
    internal async Task<int> RunAsync()
    {
        CancellationToken sessionToken = _lifetime.Token;
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        Task<Request?>? pendingRead = null;
        try
        {
            while (_state is not DapSessionState.Terminated and not DapSessionState.Faulted)
            {
                if (_cancelableRequest is { IsCompleted: true })
                {
                    await CompleteCancelableRequestAsync().ConfigureAwait(false);
                }

                Request? request;
                if (_cancelableRequest is not null || !_pendingRequests.TryDequeue(out request))
                {
                    pendingRead ??= _reader.ReadRequestAsync(readCancellation.Token).AsTask();
                    _ = await (_cancelableRequest is null
                        ? Task.WhenAny(pendingRead, _targetCompletion.Task)
                        : Task.WhenAny(pendingRead, _cancelableRequest, _targetCompletion.Task))
                        .WaitAsync(sessionToken).ConfigureAwait(false);

                    if (_targetCompletion.Task.IsCompleted)
                    {
                        break;
                    }

                    if (_cancelableRequest is { IsCompleted: true })
                    {
                        continue;
                    }

                    Task<Request?> completedRead = pendingRead;
                    pendingRead = null;
                    request = await completedRead.WaitAsync(sessionToken).ConfigureAwait(false);
                    if (request is null)
                    {
                        break;
                    }

                    if (string.Equals(request.Command, "cancel", StringComparison.Ordinal))
                    {
                        await CancelRequestAsync(request, sessionToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_cancelableRequest is not null)
                    {
                        if (!_pendingRequests.TryEnqueue(request, _reader.LastPayloadBytes))
                        {
                            await WriteRequestFailureAsync(request,
                                "The DAP pending request limit was reached. Wait for pending " +
                                    "responses or cancel queued requests before sending more work.",
                                sessionToken).ConfigureAwait(false);
                        }

                        continue;
                    }
                }

                if (IsCancelableRequest(request.Command))
                {
                    _cancelableRequestCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(sessionToken);
                    _cancelableRequestSequence = request.Seq;
                    _cancelableRequest = HandleCancelableRequestAsync(
                        request,
                        _cancelableRequestCancellation.Token).AsTask();
                    continue;
                }

                await HandleRequestAsync(request, sessionToken).ConfigureAwait(false);
            }

            if (_state is DapSessionState.Terminated or DapSessionState.Faulted)
            {
                // Target exit stops input without canceling responses already owed to the client.
                await readCancellation.CancelAsync().ConfigureAwait(false);
                await CompleteTerminatedRequestsAsync(sessionToken).ConfigureAwait(false);
                if (pendingRead is not null)
                {
                    Request? unread = await SettleProtocolReadAsync(pendingRead, readCancellation.Token)
                        .ConfigureAwait(false);
                    pendingRead = null;
                    if (unread is not null)
                    {
                        await WriteStateFailureAsync(unread, sessionToken).ConfigureAwait(false);
                    }
                }
            }

            return _state == DapSessionState.Faulted ? 1 : 0;
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            return _state == DapSessionState.Faulted ? 1 : 0;
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
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_cancelableRequest is not null)
            {
                await _cancelableRequestCancellation!.CancelAsync().ConfigureAwait(false);
                await CompleteCancelableRequestAsync().ConfigureAwait(false);
            }

            if (pendingRead is not null)
            {
                _ = await SettleProtocolReadAsync(pendingRead, sessionToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteTerminatedRequestsAsync(CancellationToken cancellationToken)
    {
        if (_cancelableRequest is not null)
        {
            await CompleteCancelableRequestAsync().ConfigureAwait(false);
        }

        while (_pendingRequests.TryDequeue(out Request? request))
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Request?> SettleProtocolReadAsync(
        Task<Request?> pendingRead,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pendingRead.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The canceled read is settled before its transport and cancellation source are disposed.
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _engineSession.DisposeAsync().ConfigureAwait(false);
        _cancelableRequestCancellation?.Dispose();
        _cancelableRequestCancellation = null;
        _lifetime.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        _stopEventGate.Dispose();
    }

    private void ClearPendingTarget()
    {
        _pendingTargetRequest = null;
        _pendingConfigurationRequest = null;
        _pendingLaunch = null;
        _pendingAttach = null;
        _pendingTargetArguments = null;
    }

    private bool IsProtocolClosed => Volatile.Read(ref _protocolClosed) != 0;

    private bool IsExpectedClosedTransportException(Exception exception) =>
        IsProtocolClosed && exception is IOException or ObjectDisposedException or OperationCanceledException;

    private async ValueTask CancelRequestAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        int? requestId;
        try
        {
            requestId = GetOptionalPositiveInteger(request.Arguments, "requestId", "cancel");
        }
        catch (ArgumentException exception)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (requestId == _cancelableRequestSequence &&
            _cancelableRequestCancellation is not null)
        {
            await _cancelableRequestCancellation.CancelAsync().ConfigureAwait(false);
        }
        else if (requestId is int sequence && _pendingRequests.TryRemove(sequence, out Request? queued))
        {
            await WriteRequestFailureAsync(queued, "cancelled", cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteCancelableRequestAsync()
    {
        Task cancelableRequest = _cancelableRequest!;
        using CancellationTokenSource cancellation = _cancelableRequestCancellation!;
        _cancelableRequest = null;
        _cancelableRequestCancellation = null;
        _cancelableRequestSequence = 0;
        try
        {
            await cancelableRequest.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // A closed connection cannot carry the final response of its canceled operation.
            return;
        }
    }

    private async ValueTask HandleCancelableRequestAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await WriteRequestFailureAsync(request, "cancelled", _lifetime.Token).ConfigureAwait(false);
        }
    }

    private static bool IsCancelableRequest(string command) =>
        command is "evaluate" or "setVariable" or "setExpression" or "variables" or
            "stackTrace" or "scopes" or "threads" or "modules" or "loadedSources" or "source" or
            "breakpointLocations" or "stepInTargets" or "gotoTargets" or "completions" or
            "readMemory" or "disassemble" or "exceptionInfo";
}
