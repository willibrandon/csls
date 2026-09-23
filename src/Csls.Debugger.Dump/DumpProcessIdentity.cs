using Microsoft.Diagnostics.Runtime;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Dump;

/// <summary>
/// Reads the original process identity recorded in a captured dump without accessing a live process.
/// </summary>
internal static class DumpProcessIdentity
{
    /// <summary>
    /// Resolves the captured identifier from the platform reader or the CoreCLR Mach-O thread-info record.
    /// </summary>
    /// <param name="reader">The already opened immutable dump reader.</param>
    /// <param name="path">The original dump file.</param>
    /// <param name="cancellationToken">Cancels bounded Mach-O command traversal.</param>
    /// <returns>The captured positive identifier, or null when the dump records no process identity.</returns>
    internal static int? Read(IDataReader reader, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.ProcessId > 0)
        {
            return reader.ProcessId;
        }
        if (reader.TargetPlatform != OSPlatform.OSX)
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        return ReadMachO(stream, cancellationToken);
    }

    /// <summary>
    /// Reads a CoreCLR process identifier from bounded Mach-O load commands while retaining caller stream ownership.
    /// </summary>
    /// <param name="stream">The seekable original dump file.</param>
    /// <param name="cancellationToken">Cancels before reading or between load commands.</param>
    /// <returns>The captured positive identifier, or null when the CoreCLR identity record is absent.</returns>
    internal static int? ReadMachO(FileStream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        uint cpu = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf ||
            cpu is not (0x01000007 or 0x0100000c) || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != 4 ||
            count > 65536 || bytes > 16 * 1024 * 1024 || count > bytes / 8 || stream.Length - 32 < bytes)
        {
            throw new InvalidDataException("The dump has an invalid or oversized Mach-O command table.");
        }

        long end = 32L + bytes;
        long position = 32;
        int? processId = null;
        Span<byte> command = stackalloc byte[72];
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - position < 8)
            {
                throw new InvalidDataException("The Mach-O load command header exceeds its recorded table.");
            }
            stream.Position = position;
            stream.ReadExactly(command[..8]);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (size < 8 || size % 8 != 0 || size > end - position)
            {
                throw new InvalidDataException("The Mach-O load command has an invalid size.");
            }

            if (kind == 0x19)
            {
                if (size < command.Length)
                {
                    throw new InvalidDataException("The Mach-O segment command is truncated.");
                }
                stream.ReadExactly(command[8..]);
                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(command[24..]);
                if (address == 0x7fffffff00000000 || cpu == 0x0100000c && address == 0x00007ffffff00000)
                {
                    int? recorded = ReadThreadInfo(stream, command, end);
                    if (recorded is not null)
                    {
                        if (processId is not null)
                        {
                            throw new InvalidDataException("The dump contains multiple CoreCLR process-identity records.");
                        }
                        processId = recorded;
                    }
                }
            }
            position += size;
        }
        if (position != end)
        {
            throw new InvalidDataException("The Mach-O load commands do not fill their recorded table.");
        }
        return processId;
    }

    private static int? ReadThreadInfo(FileStream stream, ReadOnlySpan<byte> command, long tableEnd)
    {
        ulong virtualSize = BinaryPrimitives.ReadUInt64LittleEndian(command[32..]);
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(command[40..]);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(command[48..]);
        if (offset < (ulong)tableEnd || offset > (ulong)stream.Length || size < 24 ||
            size > virtualSize || size > (ulong)stream.Length - offset)
        {
            throw new InvalidDataException("The CoreCLR thread-info segment exceeds the captured file range.");
        }
        stream.Position = checked((long)offset);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..16].SequenceEqual("THREADINFO\0\0\0\0\0\0"u8))
        {
            return null;
        }
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint threads = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (pid is 0 or > int.MaxValue || 24UL + 16UL * threads > size)
        {
            throw new InvalidDataException("The CoreCLR thread-info record has an invalid process identity or thread count.");
        }
        return (int)pid;
    }
}
