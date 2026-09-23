namespace Csls.Debugger;

/// <summary>
/// Reads a bounded captured image while accounting for the active inspection request.
/// </summary>
internal sealed class CorDebugDumpMemoryStream : Stream
{
    private readonly ICorDebugDumpSource _source;
    private readonly CorDebugDumpCallbacks _callbacks;
    private readonly ulong _address;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Borrows the captured memory source for one module metadata read.
    /// </summary>
    internal CorDebugDumpMemoryStream(ICorDebugDumpSource source, CorDebugDumpCallbacks callbacks, ulong address, ulong size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentOutOfRangeException.ThrowIfZero(address);
        ArgumentOutOfRangeException.ThrowIfZero(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 512UL * 1024 * 1024);
        _ = checked(address + size);
        _source = source;
        _callbacks = callbacks;
        _address = address;
        _length = checked((long)size);
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _callbacks.Operation?.ThrowIfInterrupted();
        int count = (int)Math.Min(buffer.Length, _length - _position);
        int read = count == 0 ? 0 : _source.ReadMemory(checked(_address + (ulong)_position), buffer[..count]);
        if (read < 0 || read > count)
        {
            throw new InvalidDataException("The captured memory reader returned an invalid byte count.");
        }
        _callbacks.Operation?.RecordMemoryRead(read);
        _callbacks.Operation?.ThrowIfInterrupted();
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > _length)
        {
            throw new IOException("The requested position is outside the captured module.");
        }
        _position = position;
        return position;
    }

    /// <inheritdoc />
    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("Captured memory is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Captured memory is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
