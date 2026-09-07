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
