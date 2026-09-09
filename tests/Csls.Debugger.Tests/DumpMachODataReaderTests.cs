using Csls.Debugger.Dump;
using System.Buffers.Binary;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Rejects hostile native core-file metadata through real files before register contexts can be associated incorrectly.
/// </summary>
[TestClass]
public sealed class DumpMachODataReaderTests : DapTestContext
{
    /// <summary>
    /// Rejects malformed or ambiguous process-metadata records instead of assigning registers to another thread.
    /// </summary>
    /// <param name="json">The hostile native note payload.</param>
    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{\"threads\":null}")]
    [DataRow("{\"threads\":[]}")]
    [DataRow("{\"threads\":[null]}")]
    [DataRow("{\"threads\":[{\"thread_id\":0}]}")]
    [DataRow("{\"threads\":[{\"thread_id\":-1}]}")]
    [DataRow("{\"threads\":[{\"thread_id\":4294967296}]}")]
    [DataRow("{\"threads\":[{\"thread_id\":1.5}]}")]
    [DataRow("{\"threads\":[{\"thread_id\":\"42\"}]}")]
    [DataRow("{\"threads\":[{\"thread_id\":42,\"thread_id\":43}]}")]
    [DataRow("{\"threads\":[{}],\"threads\":[{}]}")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidNativeThreadMetadataIsRejected(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateHostileCore(json), TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = Assert.ThrowsExactly<InvalidDataException>(() =>
                DumpMachODataReader.ReadThreadOrdinals(path, TestContext.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rejects overlapping identities, duplicated notes, and file-table boundaries before inspecting any target code.
    /// </summary>
    /// <param name="mutation">The independent malformed file boundary.</param>
    [TestMethod]
    [DataRow("duplicate-id")]
    [DataRow("duplicate-note")]
    [DataRow("note-in-header")]
    [DataRow("note-past-file")]
    [DataRow("note-too-large")]
    [DataRow("note-empty")]
    [DataRow("table-too-large")]
    [DataRow("table-trailing-bytes")]
    [DataRow("command-zero")]
    [DataRow("command-unaligned")]
    [DataRow("command-past-table")]
    [DataRow("note-short")]
    [DataRow("thread-limit")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidNativeCoreBoundsAreRejected(string mutation)
    {
        byte[] bytes = CreateHostileCore(mutation == "duplicate-id"
            ? "{\"threads\":[{\"thread_id\":42},{\"thread_id\":42}]}"
            : "{\"threads\":[{\"thread_id\":42}]}",
            mutation == "duplicate-id" ? 2 : mutation == "thread-limit" ? 4097 : 1,
            mutation == "duplicate-note" ? 2 : 1);
        switch (mutation)
        {
            case "note-in-header": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), 32); break;
            case "note-past-file": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), ulong.MaxValue); break;
            case "note-too-large": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), 1024 * 1024 + 1); break;
            case "note-empty": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), 0); break;
            case "table-too-large": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 16 * 1024 * 1024 + 1); break;
            case "table-trailing-bytes":
                byte[] expanded = new byte[bytes.Length + 8];
                bytes.AsSpan(0, 80).CopyTo(expanded);
                bytes.AsSpan(80).CopyTo(expanded.AsSpan(88));
                bytes = expanded;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 56);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), 88);
                break;
            case "command-zero": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 0); break;
            case "command-unaligned": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 39); break;
            case "command-past-table": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 128); break;
            case "note-short": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 32); break;
        }
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            _ = Assert.ThrowsExactly<InvalidDataException>(() =>
                DumpMachODataReader.ReadThreadOrdinals(path, TestContext.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rejects thread-state records whose native word counts exceed their enclosing command.
    /// </summary>
    /// <param name="commandSize">The recorded thread-command byte extent.</param>
    /// <param name="wordCount">The hostile register-state word count.</param>
    [TestMethod]
    [DataRow(12, 0u)]
    [DataRow(16, 1u)]
    [DataRow(16, uint.MaxValue)]
    [DataRow(24, 3u)]
    [DataRow(24, 1u)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidNativeThreadStateBoundsAreRejected(int commandSize, uint wordCount)
    {
        byte[] bytes = new byte[32 + commandSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x01000007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)commandSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), (uint)commandSize);
        if (commandSize >= 16)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), wordCount);
        }
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            _ = Assert.ThrowsExactly<InvalidDataException>(() =>
                DumpMachODataReader.ReadThreadOrdinals(path, TestContext.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rejects truncated, conflicting and incorrectly wrapped Intel register records in native core files.
    /// </summary>
    /// <param name="mutation">The hostile Intel register encoding.</param>
    /// <param name="message">The specific rejected register invariant.</param>
    [TestMethod]
    [DataRow("general-flavor", "wrapper has an invalid flavor or word count")]
    [DataRow("general-count", "wrapper has an invalid flavor or word count")]
    [DataRow("floating-flavor", "wrapper has an invalid flavor or word count")]
    [DataRow("floating-count", "wrapper has an invalid flavor or word count")]
    [DataRow("wrapped-size", "register state has an invalid size")]
    [DataRow("general-size", "register state has an invalid size")]
    [DataRow("floating-size", "register state has an invalid size")]
    [DataRow("duplicate-general", "repeats its Intel general register state")]
    [DataRow("duplicate-floating", "repeats its Intel floating-point register state")]
    [DataRow("missing-general", "has no general register state")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidNativeIntelRegisterRecordsAreRejected(string mutation, string message)
    {
        (uint Flavor, int Bytes, uint InnerFlavor, uint InnerCount)[] records = mutation switch
        {
            "general-flavor" => [(7, 176, 3, 42)],
            "general-count" => [(7, 176, 4, 41)],
            "floating-flavor" => [(8, 532, 4, 131)],
            "floating-count" => [(8, 532, 5, 130)],
            "wrapped-size" => [(7, 168, 4, 42)],
            "general-size" => [(4, 160, 0, 0)],
            "floating-size" => [(5, 516, 0, 0)],
            "duplicate-general" => [(7, 176, 4, 42), (4, 168, 0, 0)],
            "duplicate-floating" => [(7, 176, 4, 42), (8, 532, 5, 131), (5, 524, 0, 0)],
            "missing-general" => [(8, 532, 5, 131)],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        int size = 8 + records.Sum(static record => 8 + record.Bytes);
        byte[] bytes = new byte[32 + size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x01000007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), (uint)size);
        int position = 40;
        foreach ((uint flavor, int length, uint innerFlavor, uint innerCount) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position), flavor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 4), (uint)(length / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 8), innerFlavor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 12), innerCount);
            position += 8 + length;
        }
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            InvalidDataException rejected = Assert.ThrowsExactly<InvalidDataException>(() =>
                DumpMachODataReader.ReadThreadOrdinals(path, TestContext.CancellationToken));
            Assert.Contains(message, rejected.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateHostileCore(string json, int threads = 1, int notes = 1)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        int commandsSize = notes * 40 + threads * 8;
        byte[] bytes = new byte[32 + commandsSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x0100000c);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)(threads + notes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)commandsSize);
        for (int index = 0; index < notes; index++)
        {
            Span<byte> note = bytes.AsSpan(32 + 40 * index, 40);
            BinaryPrimitives.WriteUInt32LittleEndian(note, 0x31);
            BinaryPrimitives.WriteUInt32LittleEndian(note[4..], 40);
            "process metadata"u8.CopyTo(note[8..]);
            BinaryPrimitives.WriteUInt64LittleEndian(note[24..], (ulong)(32 + commandsSize));
            BinaryPrimitives.WriteUInt64LittleEndian(note[32..], (ulong)payload.Length);
        }
        for (int index = 0; index < threads; index++)
        {
            Span<byte> thread = bytes.AsSpan(32 + notes * 40 + 8 * index, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(thread, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(thread[4..], 8);
        }
        payload.CopyTo(bytes.AsSpan(32 + commandsSize));
        return bytes;
    }
}
