using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Compares captured Intel register contexts against the original native core-file records.
/// </summary>
internal static class DumpMachORegisterAssertions
{
    /// <summary>
    /// Verifies native register translation and bounded context copies using an actual captured target.
    /// </summary>
    /// <param name="path">The original captured process file.</param>
    /// <param name="cancellationToken">Cancels between native file records.</param>
    internal static void Verify(string path, CancellationToken cancellationToken)
    {
        var options = new DataTargetOptions { SymbolPaths = [], UseLockFreeMemoryMapReader = Environment.Is64BitProcess };
        using DataTarget target = DumpMachODataReader.Open(path, options, cancellationToken);
        if (target.DataReader.TargetPlatform != OSPlatform.OSX || target.DataReader.Architecture != Architecture.X64)
        {
            return;
        }
        IReadOnlyDictionary<uint, uint>? ordinals = DumpMachODataReader.ReadThreadOrdinals(path, cancellationToken);
        if (ordinals is null || ordinals.Count == 0)
        {
            return;
        }
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        uint commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        Assert.IsLessThanOrEqualTo(65536u, commands);
        long cursor = 32;
        uint thread = 0;
        Span<byte> record = stackalloc byte[8];
        for (uint index = 0; index < commands; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = cursor;
            stream.ReadExactly(record);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            Assert.IsGreaterThanOrEqualTo(8u, size);
            Assert.IsLessThanOrEqualTo(stream.Length, cursor + size);
            if (kind == 4)
            {
                uint id = Assert.ContainsSingle(ordinals.Where(pair => pair.Value == thread)).Key;
                AssertThread(target.DataReader, stream, cursor + size, id, cancellationToken);
                thread++;
            }
            cursor += size;
        }
        Assert.AreEqual(ordinals.Count, (int)thread);
    }

    private static void AssertThread(IDataReader reader, Stream stream, long end, uint id, CancellationToken cancellationToken)
    {
        byte[] captured = new byte[AMD64Context.Size + 8];
        Array.Fill(captured, (byte)0xa5);
        Assert.IsTrue(reader.GetThreadContext(id, 0x10000f, captured));
        Assert.IsEmpty(captured.Skip(AMD64Context.Size).Where(static value => value != 0xa5));
        AMD64Context context = MemoryMarshal.Read<AMD64Context>(captured);
        Span<byte> header = stackalloc byte[8];
        Span<byte> payload = stackalloc byte[532];
        while (stream.Position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            uint flavor = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long next = stream.Position + 4L * count;
            Assert.IsLessThanOrEqualTo(end, next);
            if (flavor is 7 or 8)
            {
                Assert.AreEqual(flavor == 7 ? 44u : 133u, count);
                Span<byte> state = payload[..checked((int)(4 * count))];
                stream.ReadExactly(state);
                Assert.AreEqual(flavor == 7 ? 4u : 5u, BinaryPrimitives.ReadUInt32LittleEndian(state));
                Assert.AreEqual(flavor == 7 ? 42u : 131u, BinaryPrimitives.ReadUInt32LittleEndian(state[4..]));
                if (flavor == 7)
                {
                    ulong[] expected = new ulong[21];
                    for (int register = 0; register < expected.Length; register++)
                    {
                        expected[register] = BinaryPrimitives.ReadUInt64LittleEndian(state[(8 + register * 8)..]);
                    }
                    ulong[] actual = [context.Rax, context.Rbx, context.Rcx, context.Rdx, context.Rdi, context.Rsi,
                        context.Rbp, context.Rsp, context.R8, context.R9, context.R10, context.R11, context.R12,
                        context.R13, context.R14, context.R15, context.Rip, (uint)context.EFlags, context.Cs, context.Fs, context.Gs];
                    Assert.AreSequenceEqual(expected, actual);
                    Assert.IsGreaterThan(0ul, context.Rip);
                    Assert.IsGreaterThan(0ul, context.Rsp);
                    Assert.AreEqual(0x100007u, context.ContextFlags & 0x100007u);
                    byte[] shortContext = new byte[AMD64Context.Size - 1];
                    Array.Fill(shortContext, (byte)0xa5);
                    Assert.IsFalse(reader.GetThreadContext(id, 0x100007, shortContext));
                    Assert.IsEmpty(shortContext.Where(static value => value != 0xa5));
                }
                else
                {
                    Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(state[40..]), context.MxCsr);
                    Assert.AreSequenceEqual(state.Slice(16, 416).ToArray(), captured.AsSpan(0x100, 416).ToArray());
                    Assert.AreEqual(0x100008u, context.ContextFlags & 0x100008u);
                }
            }
            stream.Position = next;
        }
        Assert.AreEqual(end, stream.Position);
    }
}
