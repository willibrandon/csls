using Csls.Control.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Text.Json;

namespace Csls.Control;

/// <summary>
/// Cancels control requests through an AOT-safe versioned notification.
/// </summary>
internal sealed class ControlCancellationStrategy : ICancellationStrategy
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RequestId, CancellationTokenSource> _incomingRequests = [];
    private readonly JsonRpc _rpc;

    /// <summary>
    /// Creates and registers cancellation handling for one control connection.
    /// </summary>
    /// <param name="rpc">The owning StreamJsonRpc connection.</param>
    internal ControlCancellationStrategy(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        _rpc = rpc;
        rpc.AddLocalRpcMethod(
            ControlMethods.CancelInvocation,
            new Func<ControlRpcCancellationRequest, ValueTask>(CancelIncomingRequestAsync));
    }

    /// <inheritdoc />
    public void CancelOutboundRequest(RequestId requestId)
    {
        if (!ControlRpcCancellationRequest.TryCreate(
                requestId,
                out ControlRpcCancellationRequest? request) ||
            request is null)
        {
            _rpc.Dispose();
            return;
        }

        _ = SendCancellationAsync(request);
    }

    /// <inheritdoc />
    public void OutboundRequestEnded(RequestId requestId)
    {
    }

    /// <inheritdoc />
    public void IncomingRequestStarted(
        RequestId requestId,
        CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);
        lock (_gate)
        {
            _incomingRequests.Add(requestId, cancellationTokenSource);
        }
    }

    /// <inheritdoc />
    public void IncomingRequestEnded(RequestId requestId)
    {
        lock (_gate)
        {
            _incomingRequests.Remove(requestId);
        }
    }

    private ValueTask CancelIncomingRequestAsync(ControlRpcCancellationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.TryGetRequestId(out RequestId requestId))
        {
            return ValueTask.CompletedTask;
        }

        CancellationTokenSource? cancellationSource;
        lock (_gate)
        {
            if (_incomingRequests.TryGetValue(requestId, out cancellationSource))
            {
                _incomingRequests.Remove(requestId);
            }
        }

        return cancellationSource is null
            ? ValueTask.CompletedTask
            : CancelAsync(cancellationSource);
    }

    private static async ValueTask CancelAsync(
        CancellationTokenSource cancellationSource)
    {
        try
        {
            await cancellationSource.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private async Task SendCancellationAsync(ControlRpcCancellationRequest request)
    {
        try
        {
            await _rpc.NotifyAsync(ControlMethods.CancelInvocation, request)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ConnectionLostException or
                InvalidDataException or
                IOException or
                JsonException or
                NotSupportedException or
                ObjectDisposedException or
                SocketException)
        {
            _rpc.Dispose();
        }
    }
}
