using Csls.DebugAdapter.Protocol;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Reads bounded Debug Adapter Protocol requests from a byte stream.
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
    }

    /// <summary>
    /// Reads and validates the next request or returns null at a clean end of stream.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending stream read.</param>
    /// <returns>The next validated DAP request, or null before a new header starts.</returns>
    internal async ValueTask<Request?> ReadRequestAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[_maximumHeaderBytes];
        byte[] singleByte = new byte[1];
        int headerLength = 0;
        while (true)
        {
            int count = await _input
                .ReadAsync(singleByte.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                if (headerLength == 0)
                {
                    return null;
                }

                throw new InvalidDataException("A DAP header was truncated.");
            }

            byte value = singleByte[0];
            if (value > 0x7f)
            {
                throw new InvalidDataException("DAP headers must contain only ASCII bytes.");
            }

            if (headerLength == header.Length)
            {
                throw new InvalidDataException(
                    $"DAP headers exceed the permitted {_maximumHeaderBytes}-byte limit.");
            }

            header[headerLength] = value;
            headerLength++;
            if (headerLength >= 4 &&
                header[headerLength - 4] == (byte)'\r' &&
                header[headerLength - 3] == (byte)'\n' &&
                header[headerLength - 2] == (byte)'\r' &&
                header[headerLength - 1] == (byte)'\n')
            {
                break;
            }
        }

        int payloadLength = ParseContentLength(header.AsSpan(0, headerLength - 4));
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        int payloadOffset = 0;
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

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize(
                payload,
                DapProtocolJsonSerializerContext.Default.Request);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A DAP request contains invalid JSON.", exception);
        }

        if (request is null ||
            request.Seq <= 0 ||
            !string.Equals(request.Type, "request", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.Command))
        {
            throw new InvalidDataException("A DAP request envelope is invalid.");
        }

        return request;
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
