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
    private readonly Func<string, Task> _writeErrorAsync;
    private readonly CancellationTokenSource _lifetime = new();
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
    private int? _stoppedThreadId;
    private bool _deferGotoStoppedEvent;
    private (string Reason, int? ThreadId, DebugStopGeneration Generation,
        DebugExceptionInfo? Exception)? _deferredStop;
    private Task? _evaluationRequest;
    private CancellationTokenSource? _evaluationRequestCancellation;
    private int _evaluationRequestSequence;
    private int _evaluationHandlerFinished;
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
                    .AsTask()
                    .WaitAsync(linked.Token)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                if (string.Equals(request.Command, "cancel", StringComparison.Ordinal))
                {
                    await CancelRequestAsync(request, linked.Token).ConfigureAwait(false);
                    continue;
                }

                if (_evaluationRequest is not null)
                {
                    if (!_evaluationRequest.IsCompleted &&
                        Volatile.Read(ref _evaluationHandlerFinished) != 0)
                    {
                        await _evaluationRequest.WaitAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    if (!_evaluationRequest.IsCompleted)
                    {
                        await WriteRequestFailureAsync(
                            request,
                            "A managed function evaluation is still in progress. Cancel it or " +
                                "wait for its response before sending another request.",
                            linked.Token).ConfigureAwait(false);
                        continue;
                    }

                    await CompleteEvaluationRequestAsync().ConfigureAwait(false);
                }

                if (string.Equals(request.Command, "evaluate", StringComparison.Ordinal))
                {
                    _evaluationRequestCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(linked.Token);
                    _evaluationRequestSequence = request.Seq;
                    Volatile.Write(ref _evaluationHandlerFinished, 0);
                    _evaluationRequest = HandleEvaluationRequestAsync(
                        request,
                        _evaluationRequestCancellation.Token);
                    continue;
                }

                await HandleRequestAsync(request, linked.Token).ConfigureAwait(false);
            }

            if (_evaluationRequest is not null)
            {
                await _evaluationRequestCancellation!.CancelAsync().ConfigureAwait(false);
                await CompleteEvaluationRequestAsync().ConfigureAwait(false);
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
        _evaluationRequestCancellation?.Dispose();
        _evaluationRequestCancellation = null;
        _lifetime.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
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

        if (requestId == _evaluationRequestSequence &&
            _evaluationRequestCancellation is not null)
        {
            await _evaluationRequestCancellation.CancelAsync().ConfigureAwait(false);
        }

        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteEvaluationRequestAsync()
    {
        Task evaluationRequest = _evaluationRequest!;
        CancellationTokenSource cancellation = _evaluationRequestCancellation!;
        _evaluationRequest = null;
        _evaluationRequestCancellation = null;
        _evaluationRequestSequence = 0;
        Volatile.Write(ref _evaluationHandlerFinished, 0);
        try
        {
            await evaluationRequest.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task HandleEvaluationRequestAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _evaluationHandlerFinished, 1);
        }
    }
}
