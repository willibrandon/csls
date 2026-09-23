using Csls.DebugAdapter.Protocol;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Reads bounded Debug Adapter Protocol client messages from a byte stream.
/// </summary>
internal sealed class DapMessageReader
{
    /// <summary>
    /// Gets the default inclusive DAP header size limit.
    /// </summary>
    internal const int DefaultMaximumHeaderBytes = 8 * 1024;

    /// <summary>
    /// Gets the default inclusive DAP payload size limit.
    /// </summary>
    internal const int DefaultMaximumPayloadBytes = 16 * 1024 * 1024;

    private readonly Stream _input;
    private readonly int _maximumHeaderBytes;
    private readonly int _maximumPayloadBytes;
    private readonly byte[] _header;
    private readonly byte[] _readBuffer = new byte[DefaultMaximumHeaderBytes];
    private int _readOffset;
    private int _readLength;

    /// <summary>
    /// Creates a bounded DAP request reader.
    /// </summary>
    /// <param name="input">The client-to-adapter byte stream.</param>
    /// <param name="maximumHeaderBytes">The inclusive header size limit.</param>
    /// <param name="maximumPayloadBytes">The inclusive payload size limit.</param>
    internal DapMessageReader(
        Stream input,
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);

        _input = input;
        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumPayloadBytes = maximumPayloadBytes;
        _header = new byte[maximumHeaderBytes];
    }

    /// <summary>
    /// Reads and validates the next client message or returns null at a clean end of stream.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending stream read.</param>
    /// <returns>The next validated DAP message, or null before a new header starts.</returns>
    internal async ValueTask<DapInboundMessage?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        int headerLength = 0;
        while (true)
        {
            if (_readOffset == _readLength)
            {
                _readLength = await _input
                    .ReadAsync(_readBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                _readOffset = 0;
                if (_readLength == 0)
                {
                    if (headerLength == 0)
                    {
                        return null;
                    }

                    throw new InvalidDataException("A DAP header was truncated.");
                }
            }

            byte value = _readBuffer[_readOffset++];
            if (value > 0x7f)
            {
                throw new InvalidDataException("DAP headers must contain only ASCII bytes.");
            }

            if (headerLength == _header.Length)
            {
                throw new InvalidDataException(
                    $"DAP headers exceed the permitted {_maximumHeaderBytes}-byte limit.");
            }

            _header[headerLength] = value;
            headerLength++;
            if (headerLength >= 4 &&
                _header[headerLength - 4] == (byte)'\r' &&
                _header[headerLength - 3] == (byte)'\n' &&
                _header[headerLength - 2] == (byte)'\r' &&
                _header[headerLength - 1] == (byte)'\n')
            {
                break;
            }
        }

        int payloadLength = ParseContentLength(_header.AsSpan(0, headerLength - 4));
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        int payloadOffset = Math.Min(payloadLength, _readLength - _readOffset);
        _readBuffer.AsSpan(_readOffset, payloadOffset).CopyTo(payload);
        _readOffset += payloadOffset;
        while (payloadOffset < payload.Length)
        {
            int count = await _input
                .ReadAsync(payload.AsMemory(payloadOffset), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("A DAP JSON payload was truncated.");
            }

            payloadOffset += count;
        }

        Request? request = null;
        Response? response = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("A DAP client message envelope has an invalid type.");
            }

            if (type.ValueEquals("request"))
            {
                request = JsonSerializer.Deserialize(
                    payload, DapProtocolJsonSerializerContext.Default.Request);
            }
            else if (type.ValueEquals("response"))
            {
                if (!root.TryGetProperty("success", out JsonElement success) ||
                    success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("A DAP response requires a Boolean success value.");
                }

                response = JsonSerializer.Deserialize(
                    payload, DapProtocolJsonSerializerContext.Default.Response);
            }
            else
            {
                throw new InvalidDataException("A DAP client message envelope must be a request or response.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A DAP client message contains invalid JSON.", exception);
        }

        if (request is not null &&
            (request.Seq <= 0 ||
                !string.Equals(request.Type, "request", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(request.Command)))
        {
            throw new InvalidDataException("A DAP request envelope is invalid.");
        }

        if (response is not null &&
            (response.Seq <= 0 || response.RequestSeq <= 0 ||
                !string.Equals(response.Type, "response", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.Command)))
        {
            throw new InvalidDataException("A DAP response envelope is invalid.");
        }

        if (request is null && response is null)
        {
            throw new InvalidDataException("A DAP client message envelope is invalid.");
        }

        return new DapInboundMessage(request, response, payloadLength);
    }

    private int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        string header = Encoding.ASCII.GetString(headerBytes);
        int? contentLength = null;
        foreach (string line in header.Split("\r\n", StringSplitOptions.None))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException("A DAP header is missing its name delimiter.");
            }

            if (!line.AsSpan(0, colon).Equals(
                    "Content-Length".AsSpan(),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (contentLength.HasValue)
            {
                throw new InvalidDataException(
                    "A DAP message contains duplicate Content-Length headers.");
            }

            if (!int.TryParse(
                    line.AsSpan(colon + 1).Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parsedLength))
            {
                throw new InvalidDataException("The DAP Content-Length value is invalid.");
            }

            contentLength = parsedLength;
        }

        if (!contentLength.HasValue)
        {
            throw new InvalidDataException("A DAP message is missing its Content-Length header.");
        }

        if (contentLength.Value <= 0 || contentLength.Value > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"DAP payload length {contentLength.Value} is outside the permitted range " +
                $"of 1 to {_maximumPayloadBytes} bytes.");
        }

        return contentLength.Value;
    }
}
