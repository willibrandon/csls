using System.Buffers.Binary;

namespace Csls.Control;

/// <summary>
/// Validates length-prefixed control payloads before allowing their bytes to be read.
/// </summary>
public sealed class BoundedMessageStream : Stream
{
    private readonly Stream _innerStream;
    private readonly int _maximumMessageBytes;
    private readonly bool _leaveOpen;
    private readonly byte[] _header = new byte[sizeof(int)];
    private int _headerBytesRead;
    private int _payloadBytesRemaining;

    /// <summary>
    /// Creates a bidirectional stream that bounds each inbound length-prefixed message.
    /// </summary>
    /// <param name="innerStream">The underlying bidirectional control stream.</param>
    /// <param name="maximumMessageBytes">The inclusive maximum payload size.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves the inner stream open.</param>
    public BoundedMessageStream(
        Stream innerStream,
        int maximumMessageBytes,
        bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        _innerStream = innerStream;
        _maximumMessageBytes = maximumMessageBytes;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override bool CanRead => _innerStream.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _innerStream.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _innerStream.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _innerStream.FlushAsync(cancellationToken);

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
        _innerStream.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) =>
        _innerStream.Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _innerStream.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => base.DisposeAsync();

    private int GetPermittedReadCount(int requestedCount)
    {
        if (requestedCount <= 0)
        {
            return requestedCount;
        }

        if (_headerBytesRead == sizeof(int) && _payloadBytesRemaining == 0)
        {
            _headerBytesRead = 0;
        }

        return _headerBytesRead < sizeof(int)
            ? Math.Min(requestedCount, sizeof(int) - _headerBytesRead)
            : Math.Min(requestedCount, _payloadBytesRemaining);
    }

    private void ObserveRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        if (_headerBytesRead < sizeof(int))
        {
            bytes.CopyTo(_header.AsSpan(_headerBytesRead));
            _headerBytesRead += bytes.Length;
            if (_headerBytesRead == sizeof(int))
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(_header);
                if (length is <= 0 || length > _maximumMessageBytes)
                {
                    throw new InvalidDataException(
                        $"Control payload length {length} is outside the permitted range.");
                }

                _payloadBytesRemaining = length;
            }

            return;
        }

        _payloadBytesRemaining -= bytes.Length;
    }
}
