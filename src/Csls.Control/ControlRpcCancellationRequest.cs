using StreamJsonRpc;
using System.Text.Json.Serialization;

namespace Csls.Control;

/// <summary>
/// Carries one numeric or textual control request identifier without reflection.
/// </summary>
internal sealed class ControlRpcCancellationRequest
{
    /// <summary>
    /// Gets the numeric request identifier, when one was assigned.
    /// </summary>
    [JsonPropertyName("number")]
    public long? Number { get; init; }

    /// <summary>
    /// Gets the textual request identifier, when one was assigned.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Creates a serializable request from a specified StreamJsonRpc identifier.
    /// </summary>
    /// <param name="requestId">The outbound request identifier.</param>
    /// <param name="request">The serializable cancellation request.</param>
    /// <returns>True when the identifier can be represented.</returns>
    internal static bool TryCreate(
        RequestId requestId,
        out ControlRpcCancellationRequest? request)
    {
        if (requestId.Number is long number)
        {
            request = new ControlRpcCancellationRequest { Number = number };
            return true;
        }

        if (requestId.String is string text)
        {
            request = new ControlRpcCancellationRequest { Text = text };
            return true;
        }

        request = null;
        return false;
    }

    /// <summary>
    /// Recreates the represented StreamJsonRpc request identifier.
    /// </summary>
    /// <param name="requestId">The represented request identifier.</param>
    /// <returns>True when exactly one valid identifier value was present.</returns>
    internal bool TryGetRequestId(out RequestId requestId)
    {
        if (Number is long number && Text is null)
        {
            requestId = new RequestId(number);
            return true;
        }

        if (Number is null && Text is not null)
        {
            requestId = new RequestId(Text);
            return true;
        }

        requestId = RequestId.NotSpecified;
        return false;
    }
}
