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
    private readonly CancellationTokenSource _lifetime = new();
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
                    pendingRead ??= _reader.ReadRequestAsync(linked.Token).AsTask();
                    if (_cancelableRequest is not null)
                    {
                        _ = await Task.WhenAny(pendingRead, _cancelableRequest)
                            .WaitAsync(linked.Token).ConfigureAwait(false);
                        if (_cancelableRequest.IsCompleted)
                        {
                            continue;
                        }
                    }

                    Task<Request?> completedRead = pendingRead;
                    pendingRead = null;
                    request = await completedRead.WaitAsync(linked.Token).ConfigureAwait(false);
                    if (request is null)
                    {
                        break;
                    }

                    if (string.Equals(request.Command, "cancel", StringComparison.Ordinal))
                    {
                        await CancelRequestAsync(request, linked.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (_cancelableRequest is not null)
                    {
                        if (!_pendingRequests.TryEnqueue(request, _reader.LastPayloadBytes))
                        {
                            await WriteRequestFailureAsync(request,
                                "The DAP pending request limit was reached. Wait for pending " +
                                    "responses or cancel queued requests before sending more work.",
                                linked.Token).ConfigureAwait(false);
                        }

                        continue;
                    }
                }

                if (IsCancelableTargetCodeRequest(request.Command))
                {
                    _cancelableRequestCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(linked.Token);
                    _cancelableRequestSequence = request.Seq;
                    _cancelableRequest = HandleRequestAsync(
                        request,
                        _cancelableRequestCancellation.Token).AsTask();
                    continue;
                }

                await HandleRequestAsync(request, linked.Token).ConfigureAwait(false);
            }

            return _state == DapSessionState.Faulted ? 1 : 0;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
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
            try
            {
                await linked.CancelAsync().ConfigureAwait(false);
                if (_cancelableRequest is not null)
                {
                    await _cancelableRequestCancellation!.CancelAsync().ConfigureAwait(false);
                    await CompleteCancelableRequestAsync().ConfigureAwait(false);
                }

                if (pendingRead is not null)
                {
                    await SettleProtocolReadAsync(pendingRead, linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref _protocolClosed, 1);
            }
        }
    }

    private static async Task SettleProtocolReadAsync(
        Task<Request?> pendingRead,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await pendingRead.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The canceled read is settled before its transport and cancellation source are disposed.
            return;
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
        await cancelableRequest.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsCancelableTargetCodeRequest(string command) =>
        command is "evaluate" or "setVariable" or "setExpression" or "variables";
}
