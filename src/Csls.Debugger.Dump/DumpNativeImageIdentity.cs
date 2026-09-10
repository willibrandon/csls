using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Dump;

/// <summary>
/// Reads bounded native ELF build identifiers and Mach-O UUIDs without loading executable code.
/// </summary>
internal static class DumpNativeImageIdentity
{
    /// <summary>
    /// Matches a native library's platform architecture and recorded build identity.
    /// </summary>
    /// <param name="path">The local native image.</param>
    /// <param name="platform">The image platform.</param>
    /// <param name="architecture">The required processor architecture.</param>
    /// <param name="identity">The required build identifier or UUID.</param>
    /// <returns>Whether the image exactly matches the requested identity.</returns>
    internal static bool Matches(string path, OSPlatform platform, Architecture architecture, ReadOnlySpan<byte> identity)
    {
        if (identity.IsEmpty || identity.Length > 64)
        {
            return false;
        }

        try
        {
            using FileStream stream = DebuggerInputFile.OpenRead(path);
            if (!stream.CanSeek)
            {
                return false;
            }
            return platform == OSPlatform.Linux ? MatchesElf(stream, architecture, identity) :
                platform == OSPlatform.OSX && MatchesMachO(stream, architecture, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return false;
        }
    }

    private static bool MatchesElf(FileStream stream, Architecture architecture, ReadOnlySpan<byte> identity)
    {
        Span<byte> header = stackalloc byte[64];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("\u007fELF"u8) || header[4] != 2 || header[5] != 1)
        {
            return false;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if ((architecture != Architecture.X64 || machine != 62) &&
            (architecture != Architecture.Arm64 || machine != 183))
        {
            return false;
        }

        ulong table = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        ushort entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[54..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(header[56..]);
        if (entrySize < 56 || count > 4096 || !Contains(stream, table, (ulong)entrySize * count))
        {
            return false;
        }

        Span<byte> entry = stackalloc byte[56];
        for (int index = 0; index < count; index++)
        {
            stream.Position = checked((long)(table + (ulong)index * entrySize));
            stream.ReadExactly(entry);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != 4)
            {
                continue;
            }

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
            if (length > 1024 * 1024 || !Contains(stream, offset, length))
            {
                return false;
            }

            byte[] notes = new byte[checked((int)length)];
            stream.Position = checked((long)offset);
            stream.ReadExactly(notes);
            for (int position = 0; position <= notes.Length - 12;)
            {
                ReadOnlySpan<byte> note = notes.AsSpan(position);
                uint nameSize = BinaryPrimitives.ReadUInt32LittleEndian(note);
                uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(note[4..]);
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(note[8..]);
                long valueOffset = 12 + (((long)nameSize + 3) & ~3L);
                long next = valueOffset + (((long)valueSize + 3) & ~3L);
                if (next > note.Length)
                {
                    return false;
                }

                if (type == 3 && nameSize == 4 && note.Slice(12, 4).SequenceEqual("GNU\0"u8))
                {
                    return note.Slice((int)valueOffset, (int)valueSize).SequenceEqual(identity);
                }

                position += checked((int)next);
            }
        }

        return false;
    }

    private static bool MatchesMachO(FileStream stream, Architecture architecture, ReadOnlySpan<byte> identity)
    {
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf)
        {
            return false;
        }

        uint cpu = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if ((architecture != Architecture.X64 || cpu != 0x01000007) &&
            (architecture != Architecture.Arm64 || cpu != 0x0100000c))
        {
            return false;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (count > 4096 || size > 1024 * 1024 || !Contains(stream, 32, size))
        {
            return false;
        }

        byte[] commands = new byte[checked((int)size)];
        stream.ReadExactly(commands);
        int offset = 0;
        for (int index = 0; index < count; index++)
        {
            if (offset > commands.Length - 8)
            {
                return false;
            }

            ReadOnlySpan<byte> command = commands.AsSpan(offset);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (length < 8 || length > command.Length)
            {
                return false;
            }

            if (kind == 0x1b)
            {
                return length == 24 && command.Slice(8, 16).SequenceEqual(identity);
            }

            offset += checked((int)length);
        }

        return false;
    }

    private static bool Contains(FileStream stream, ulong offset, ulong count) =>
        offset <= (ulong)stream.Length && count <= (ulong)stream.Length - offset;
}
