using Csls.DebugAdapter.Protocol;

namespace Csls.DebugAdapter;

/// <summary>
/// Carries one validated client request or reverse-request response from the DAP stream.
/// </summary>
internal sealed class DapInboundMessage
{
    /// <summary>
    /// Creates a validated inbound message.
    /// </summary>
    /// <param name="request">The client request, when this is a request.</param>
    /// <param name="response">The client response, when this answers an adapter request.</param>
    /// <param name="payloadBytes">The framed JSON payload length.</param>
    internal DapInboundMessage(Request? request, Response? response, int payloadBytes)
    {
        Request = request;
        Response = response;
        PayloadBytes = payloadBytes;
    }

    /// <summary>
    /// Gets the client request, when present.
    /// </summary>
    internal Request? Request { get; }

    /// <summary>
    /// Gets the response to an adapter request, when present.
    /// </summary>
    internal Response? Response { get; }

    /// <summary>
    /// Gets the framed JSON payload length.
    /// </summary>
    internal int PayloadBytes { get; }
}
