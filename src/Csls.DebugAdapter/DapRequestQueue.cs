using Csls.DebugAdapter.Protocol;
using System.Diagnostics.CodeAnalysis;

namespace Csls.DebugAdapter;

/// <summary>
/// Bounds pending DAP requests by both count and wire size while preserving their arrival order.
/// </summary>
internal sealed class DapRequestQueue
{
    private const int MaximumRequests = 64;
    private const int MaximumPayloadBytes = DapMessageReader.DefaultMaximumPayloadBytes;
    private readonly Queue<(Request Request, int PayloadBytes)> _requests = new();
    private int _payloadBytes;

    /// <summary>
    /// Retains a request only when both pending-request bounds permit it.
    /// </summary>
    /// <param name="request">The validated request read from the client.</param>
    /// <param name="payloadBytes">The complete wire payload size measured by the reader.</param>
    /// <returns>Whether the request was accepted into the queue.</returns>
    internal bool TryEnqueue(Request request, int payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadBytes);
        if (_requests.Count >= MaximumRequests || payloadBytes > MaximumPayloadBytes - _payloadBytes)
        {
            return false;
        }

        _requests.Enqueue((request, payloadBytes));
        _payloadBytes += payloadBytes;
        return true;
    }

    /// <summary>
    /// Removes the oldest accepted request and releases its retained payload budget.
    /// </summary>
    /// <param name="request">The oldest pending request, or null when the queue is empty.</param>
    /// <returns>Whether a request was removed.</returns>
    internal bool TryDequeue([NotNullWhen(true)] out Request? request)
    {
        if (_requests.TryDequeue(out (Request Request, int PayloadBytes) item))
        {
            _payloadBytes -= item.PayloadBytes;
            request = item.Request;
            return true;
        }

        request = null;
        return false;
    }

    /// <summary>
    /// Cancels a queued request without disturbing the order of the surviving requests.
    /// </summary>
    /// <param name="sequence">The client sequence number identifying the request.</param>
    /// <param name="request">The removed request, or null if it is no longer queued.</param>
    /// <returns>Whether a queued request was removed.</returns>
    internal bool TryRemove(int sequence, [NotNullWhen(true)] out Request? request)
    {
        request = null;
        int count = _requests.Count;
        for (int index = 0; index < count; index++)
        {
            (Request Request, int PayloadBytes) item = _requests.Dequeue();
            if (request is null && item.Request.Seq == sequence)
            {
                request = item.Request;
                _payloadBytes -= item.PayloadBytes;
            }
            else
            {
                _requests.Enqueue(item);
            }
        }

        return request is not null;
    }
}
