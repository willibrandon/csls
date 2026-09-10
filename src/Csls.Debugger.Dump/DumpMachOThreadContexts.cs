using Microsoft.Diagnostics.Runtime;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Dump;

/// <summary>
/// Decodes bounded native Intel thread-state wrappers into captured runtime register contexts.
/// </summary>
internal static class DumpMachOThreadContexts
{
    /// <summary>
    /// Validates native register records and translates wrapped Intel general and floating-point state.
    /// </summary>
    /// <param name="stream">The borrowed original core-file stream.</param>
    /// <param name="cpu">The core-file CPU type.</param>
    /// <param name="position">The first register record after the thread-command header.</param>
    /// <param name="end">The exclusive end of the enclosing thread command.</param>
    /// <param name="cancellationToken">Cancels between bounded register records.</param>
    /// <returns>The translated context, or null for thread formats handled by the native dump reader.</returns>
    internal static byte[]? Read(Stream stream, uint cpu, long position, long end, CancellationToken cancellationToken)
    {
        byte[]? context = cpu == 0x01000007 ? new byte[AMD64Context.Size] : null;
        bool general = false;
        bool floating = false;
        bool wrapped = false;
        long start = position;
        Span<byte> header = stackalloc byte[8];
        Span<byte> payload = stackalloc byte[532];
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - position < header.Length)
            {
                // Native structure writers can round the completed register records up to eight bytes.
                if (general && end - position == 4 && (position - start) % 8 == 4)
                {
                    stream.Position = position;
                    stream.ReadExactly(header[..4]);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0)
                    {
                        throw new InvalidDataException("The Mach-O thread-state alignment padding is not zero.");
                    }
                    break;
                }
                throw new InvalidDataException("The Mach-O thread-state header exceeds its command.");
            }
            stream.Position = position;
            stream.ReadExactly(header);
            uint flavor = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (count > (end - position - header.Length) / 4)
            {
                throw new InvalidDataException("The Mach-O thread-state word count exceeds its command.");
            }
            long length = 4L * count;
            if (context is not null && flavor is 4 or 5 or 7 or 8)
            {
                int expectedLength = flavor switch { 4 => 168, 5 => 524, 7 => 176, _ => 532 };
                if (length != expectedLength)
                {
                    throw new InvalidDataException("The Mach-O Intel register state has an invalid size.");
                }
                Span<byte> state = payload[..expectedLength];
                stream.ReadExactly(state);
                if (flavor is 7 or 8)
                {
                    uint expectedFlavor = flavor == 7 ? 4u : 5u;
                    uint expectedCount = flavor == 7 ? 42u : 131u;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(state) != expectedFlavor ||
                        BinaryPrimitives.ReadUInt32LittleEndian(state[4..]) != expectedCount)
                    {
                        throw new InvalidDataException("The Mach-O Intel register wrapper has an invalid flavor or word count.");
                    }
                    wrapped = true;
                    state = state[8..];
                    flavor = expectedFlavor;
                }
                if (flavor == 4)
                {
                    if (general)
                    {
                        throw new InvalidDataException("The Mach-O thread repeats its Intel general register state.");
                    }
                    general = true;
                    ReadGeneralRegisters(state, context);
                }
                else
                {
                    if (floating)
                    {
                        throw new InvalidDataException("The Mach-O thread repeats its Intel floating-point register state.");
                    }
                    floating = true;
                    ReadFloatingPointRegisters(state, context);
                }
            }
            position += header.Length + length;
        }
        if (wrapped && !general)
        {
            throw new InvalidDataException("The Mach-O Intel thread has no general register state.");
        }
        return wrapped ? context : null;
    }

    private static void ReadGeneralRegisters(ReadOnlySpan<byte> state, Span<byte> bytes)
    {
        ref AMD64Context context = ref MemoryMarshal.AsRef<AMD64Context>(bytes);
        context.ContextFlags |= AMD64Context.ContextControl | AMD64Context.ContextInteger | AMD64Context.ContextSegments;
        context.Rax = BinaryPrimitives.ReadUInt64LittleEndian(state);
        context.Rbx = BinaryPrimitives.ReadUInt64LittleEndian(state[8..]);
        context.Rcx = BinaryPrimitives.ReadUInt64LittleEndian(state[16..]);
        context.Rdx = BinaryPrimitives.ReadUInt64LittleEndian(state[24..]);
        context.Rdi = BinaryPrimitives.ReadUInt64LittleEndian(state[32..]);
        context.Rsi = BinaryPrimitives.ReadUInt64LittleEndian(state[40..]);
        context.Rbp = BinaryPrimitives.ReadUInt64LittleEndian(state[48..]);
        context.Rsp = BinaryPrimitives.ReadUInt64LittleEndian(state[56..]);
        context.R8 = BinaryPrimitives.ReadUInt64LittleEndian(state[64..]);
        context.R9 = BinaryPrimitives.ReadUInt64LittleEndian(state[72..]);
        context.R10 = BinaryPrimitives.ReadUInt64LittleEndian(state[80..]);
        context.R11 = BinaryPrimitives.ReadUInt64LittleEndian(state[88..]);
        context.R12 = BinaryPrimitives.ReadUInt64LittleEndian(state[96..]);
        context.R13 = BinaryPrimitives.ReadUInt64LittleEndian(state[104..]);
        context.R14 = BinaryPrimitives.ReadUInt64LittleEndian(state[112..]);
        context.R15 = BinaryPrimitives.ReadUInt64LittleEndian(state[120..]);
        context.Rip = BinaryPrimitives.ReadUInt64LittleEndian(state[128..]);
        context.EFlags = BinaryPrimitives.ReadInt32LittleEndian(state[136..]);
        context.Cs = BinaryPrimitives.ReadUInt16LittleEndian(state[144..]);
        context.Fs = BinaryPrimitives.ReadUInt16LittleEndian(state[152..]);
        context.Gs = BinaryPrimitives.ReadUInt16LittleEndian(state[160..]);
    }

    private static void ReadFloatingPointRegisters(ReadOnlySpan<byte> state, Span<byte> bytes)
    {
        ref AMD64Context context = ref MemoryMarshal.AsRef<AMD64Context>(bytes);
        context.ContextFlags |= AMD64Context.ContextFloatingPoint;
        context.MxCsr = BinaryPrimitives.ReadUInt32LittleEndian(state[32..]);
        // Native state starts with two reserved words, followed by the FXSAVE control fields,
        // eight 16-byte x87 slots and sixteen 16-byte XMM slots. Keep context padding zeroed.
        state.Slice(8, 416).CopyTo(bytes[0x100..]);
    }
}
