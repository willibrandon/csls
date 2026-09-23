using System.Buffers.Binary;

namespace Csls.Debugger.Dump;

/// <summary>
/// Identifies captured stack storage affected by the Windows minidump memory-filter contract.
/// </summary>
internal sealed class DumpMemoryFilter
{
    private readonly bool _filtered;
    private readonly IReadOnlyList<(ulong Start, ulong End)> _ranges;

    private DumpMemoryFilter(bool filtered, IReadOnlyList<(ulong Start, ulong End)> ranges)
    {
        _filtered = filtered;
        _ranges = ranges;
    }

    /// <summary>
    /// Reads bounded minidump thread descriptors while leaving other dump formats to their native reader.
    /// </summary>
    /// <param name="path">The selected captured process file.</param>
    /// <param name="cancellationToken">Cancels header and thread-table traversal.</param>
    /// <returns>The immutable storage-filter description.</returns>
    internal static DumpMemoryFilter Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];
        int read = stream.ReadAtLeast(header[..4], 4, throwOnEndOfStream: false);
        if (read < 4 || !header[..4].SequenceEqual("MDMP"u8))
        {
            return new DumpMemoryFilter(false, []);
        }
        if (stream.Length < header.Length)
        {
            throw new InvalidDataException("The minidump header is truncated.");
        }
        stream.ReadExactly(header[4..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0xa793)
        {
            throw new InvalidDataException("The minidump header is truncated or has an invalid version.");
        }
        if ((BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) & 8) == 0)
        {
            return new DumpMemoryFilter(false, []);
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint directory = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (count > 65536 || directory < header.Length || directory > stream.Length ||
            12UL * count > (ulong)stream.Length - directory)
        {
            throw new InvalidDataException("The filtered minidump stream directory is invalid or oversized.");
        }

        List<(ulong Start, ulong End)> ranges = [];
        bool hasThreads = false;
        Span<byte> entry = stackalloc byte[12];
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = directory + 12L * index;
            stream.ReadExactly(entry);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (kind is 3 or 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                ReadThreads(stream, offset, size, kind == 3 ? 48 : 64, ranges, cancellationToken);
                hasThreads = true;
            }
        }
        if (!hasThreads)
        {
            throw new InvalidDataException("The filtered minidump has no thread storage descriptors.");
        }
        return new DumpMemoryFilter(true, ranges.AsReadOnly());
    }

    /// <summary>
    /// Reports filtered stack bytes and register values whose unwound storage cannot be proven intact.
    /// </summary>
    /// <param name="address">The runtime value's storage address, or zero for register storage.</param>
    /// <param name="size">The runtime value's storage size.</param>
    /// <returns>Whether the original value cannot be recovered reliably from its captured storage.</returns>
    internal bool Contains(ulong address, ulong size) => _filtered &&
        (address == 0 || size == 0 || size > ulong.MaxValue - address ||
        _ranges.Any(range => address < range.End && address + size > range.Start));

    private static void ReadThreads(FileStream stream, uint offset, uint size, int stride,
        List<(ulong Start, ulong End)> ranges, CancellationToken cancellationToken)
    {
        if (size < 4 || offset < 32 || offset > stream.Length || size > (ulong)stream.Length - offset)
        {
            throw new InvalidDataException("The filtered minidump thread table exceeds the captured file.");
        }
        stream.Position = offset;
        Span<byte> row = stackalloc byte[64];
        stream.ReadExactly(row[..4]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(row);
        if (count > 4096 || 4UL + (ulong)stride * count > size)
        {
            throw new InvalidDataException("The filtered minidump thread table is truncated or exceeds 4096 threads.");
        }
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(row[..stride]);
            AddRange(stream.Length, row[24..40], ranges);
            if (stride == 64)
            {
                AddRange(stream.Length, row[48..64], ranges);
            }
        }
    }

    private static void AddRange(long fileLength, ReadOnlySpan<byte> descriptor, List<(ulong Start, ulong End)> ranges)
    {
        ulong start = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]);
        if (size == 0)
        {
            return;
        }
        if (size > ulong.MaxValue - start || offset > fileLength || size > (ulong)fileLength - offset || ranges.Count == 8192)
        {
            throw new InvalidDataException("The filtered minidump stack range is invalid or exceeds the storage budget.");
        }
        ranges.Add((start, start + size));
    }
}
