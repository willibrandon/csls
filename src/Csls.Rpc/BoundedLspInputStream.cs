using System.Buffers.Text;
using System.Text;

namespace Csls.Rpc;

/// <summary>
/// Validates LSP headers and payload lengths before exposing inbound bytes.
/// </summary>
internal sealed class BoundedLspInputStream : Stream
{
    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    private static ReadOnlySpan<byte> LineTerminator => "\r\n"u8;

    private static ReadOnlySpan<byte> ContentLengthHeaderName => "Content-Length"u8;

    private readonly Stream _innerStream;
    private readonly int _maximumHeaderBytes;
    private readonly int _maximumPayloadBytes;
    private readonly bool _leaveOpen;
    private readonly byte[] _header;
    private int _headerBytesRead;
    private int _payloadBytesRemaining;

    /// <summary>
    /// Creates a read-only stream that bounds every inbound LSP message.
    /// </summary>
    /// <param name="innerStream">The underlying client-to-server stream.</param>
    /// <param name="maximumHeaderBytes">The inclusive maximum header size.</param>
    /// <param name="maximumPayloadBytes">The inclusive maximum payload size.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves the inner stream open.</param>
    internal BoundedLspInputStream(
        Stream innerStream,
        int maximumHeaderBytes,
        int maximumPayloadBytes,
        bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);

        _innerStream = innerStream;
        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumPayloadBytes = maximumPayloadBytes;
        _leaveOpen = leaveOpen;
        _header = new byte[maximumHeaderBytes];
    }

    /// <inheritdoc />
    public override bool CanRead => _innerStream.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int permittedCount = GetPermittedReadCount(buffer.Length);
        int bytesRead = _innerStream.Read(buffer[..permittedCount]);
        ObserveRead(buffer[..bytesRead]);
        return bytesRead;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int permittedCount = GetPermittedReadCount(buffer.Length);
        int bytesRead = await _innerStream
            .ReadAsync(buffer[..permittedCount], cancellationToken)
            .ConfigureAwait(false);
        ObserveRead(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }

        base.Dispose(disposing);
    }

    private int GetPermittedReadCount(int requestedCount)
    {
        if (requestedCount <= 0)
        {
            return requestedCount;
        }

        if (_payloadBytesRemaining > 0)
        {
            return Math.Min(requestedCount, _payloadBytesRemaining);
        }

        int headerBytesRemaining = _maximumHeaderBytes - _headerBytesRead;
        if (headerBytesRemaining == 0)
        {
            throw new InvalidDataException(
                $"LSP headers exceed the permitted {_maximumHeaderBytes}-byte limit.");
        }

        return Math.Min(requestedCount, headerBytesRemaining);
    }

    private void ObserveRead(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (_payloadBytesRemaining > 0)
            {
                int payloadBytes = Math.Min(
                    bytes.Length - offset,
                    _payloadBytesRemaining);
                offset += payloadBytes;
                _payloadBytesRemaining -= payloadBytes;
                if (_payloadBytesRemaining == 0)
                {
                    _headerBytesRead = 0;
                }

                continue;
            }

            ObserveHeaderByte(bytes[offset]);
            offset++;
        }
    }

    private void ObserveHeaderByte(byte value)
    {
        if (value > 0x7f)
        {
            throw new InvalidDataException("LSP headers must contain only ASCII bytes.");
        }

        if (_headerBytesRead == _maximumHeaderBytes)
        {
            throw new InvalidDataException(
                $"LSP headers exceed the permitted {_maximumHeaderBytes}-byte limit.");
        }

        _header[_headerBytesRead] = value;
        _headerBytesRead++;
        if (_headerBytesRead < HeaderTerminator.Length ||
            !_header.AsSpan(_headerBytesRead - HeaderTerminator.Length, HeaderTerminator.Length)
                .SequenceEqual(HeaderTerminator))
        {
            return;
        }

        ReadOnlySpan<byte> header = _header.AsSpan(
            0,
            _headerBytesRead - HeaderTerminator.Length);
        _payloadBytesRemaining = ParseContentLength(header);
    }

    private int ParseContentLength(ReadOnlySpan<byte> header)
    {
        int? contentLength = null;
        while (!header.IsEmpty)
        {
            int lineTerminator = header.IndexOf(LineTerminator);
            ReadOnlySpan<byte> line = lineTerminator < 0
                ? header
                : header[..lineTerminator];
            header = lineTerminator < 0
                ? []
                : header[(lineTerminator + LineTerminator.Length)..];

            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                throw new InvalidDataException("An LSP header is missing its name delimiter.");
            }

            ReadOnlySpan<byte> name = line[..colon];
            if (!Ascii.EqualsIgnoreCase(name, ContentLengthHeaderName))
            {
                continue;
            }

            if (contentLength.HasValue)
            {
                throw new InvalidDataException("An LSP message contains duplicate Content-Length headers.");
            }

            ReadOnlySpan<byte> value = TrimHeaderValue(line[(colon + 1)..]);
            if (!Utf8Parser.TryParse(value, out int parsedLength, out int bytesConsumed) ||
                bytesConsumed != value.Length)
            {
                throw new InvalidDataException("The LSP Content-Length value is invalid.");
            }

            contentLength = parsedLength;
        }

        if (!contentLength.HasValue)
        {
            throw new InvalidDataException("An LSP message is missing its Content-Length header.");
        }

        if (contentLength.Value is <= 0 || contentLength.Value > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"LSP payload length {contentLength.Value} is outside the permitted " +
                $"range of 1 to {_maximumPayloadBytes} bytes.");
        }

        return contentLength.Value;
    }

    private static ReadOnlySpan<byte> TrimHeaderValue(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t')
        {
            value = value[1..];
        }

        while (!value.IsEmpty && value[^1] is (byte)' ' or (byte)'\t')
        {
            value = value[..^1];
        }

        return value;
    }
}
