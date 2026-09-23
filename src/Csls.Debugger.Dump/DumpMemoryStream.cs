using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Presents one bounded captured address range as a seekable read-only stream.
/// </summary>
internal sealed class DumpMemoryStream : Stream
{
    private readonly IDataReader _reader;
    private readonly ulong _address;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Binds the stream to a captured module range without owning the dump reader.
    /// </summary>
    /// <param name="reader">The reader supplying captured memory only.</param>
    /// <param name="address">The captured base address of the range.</param>
    /// <param name="length">The bounded byte length of the range.</param>
    internal DumpMemoryStream(IDataReader reader, ulong address, long length)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _ = checked(address + (ulong)length);
        _reader = reader;
        _address = address;
        _length = length;
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
        int count = (int)Math.Min(buffer.Length, _length - _position);
        int read = count == 0 ? 0 : _reader.Read(checked(_address + (ulong)_position), buffer[..count]);
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
            throw new IOException("The requested position is outside the captured memory range.");
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
