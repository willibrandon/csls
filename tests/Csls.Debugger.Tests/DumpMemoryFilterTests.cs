using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded filtering metadata parsing through real captures and hostile file boundaries.
/// </summary>
[TestClass]
public sealed class DumpMemoryFilterTests : DapTestContext
{
    /// <summary>
    /// Keeps captured image memory eligible while identifying unproven register and overflowing storage in filtered captures.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FilteredWindowsDumpPreservesImageStorage()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        var filter = DumpMemoryFilter.Read(fixture.DumpPath, TestContext.CancellationToken);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        ClrInfo runtime = Assert.ContainsSingle(target.ClrVersions);
        ulong image = runtime.ModuleInfo.ImageBase;
        Assert.IsGreaterThan(0UL, image);
        Assert.IsFalse(filter.Contains(image, 4));
        Assert.IsTrue(filter.Contains(0, 4));
        Assert.IsTrue(filter.Contains(image, 0));
        Assert.IsTrue(filter.Contains(ulong.MaxValue, 2));
        Span<byte> magic = stackalloc byte[2];
        Assert.AreEqual(magic.Length, target.DataReader.Read(image, magic));
        Assert.IsTrue(magic.SequenceEqual("MZ"u8));
    }

    /// <summary>
    /// Keeps Unix triage captures eligible for inspection and honors cancellation before opening files.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task UnixTriagePreservesCapturedStorage()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        var filter = DumpMemoryFilter.Read(fixture.DumpPath, TestContext.CancellationToken);
        Assert.IsFalse(filter.Contains(0, 4));
        Assert.IsFalse(filter.Contains(ulong.MaxValue, 8));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
            DumpMemoryFilter.Read(fixture.DumpPath, cancellation.Token));
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
    }

    /// <summary>
    /// Rejects malformed filtered dump structures before any native reader consumes their address ranges.
    /// </summary>
    /// <param name="mutation">The independently malformed header, directory, or memory descriptor.</param>
    [TestMethod]
    [DataRow("header")]
    [DataRow("version")]
    [DataRow("streams")]
    [DataRow("directory")]
    [DataRow("no-threads")]
    [DataRow("table-offset")]
    [DataRow("table-length")]
    [DataRow("thread-count")]
    [DataRow("thread-length")]
    [DataRow("address-overflow")]
    [DataRow("memory-offset")]
    [DataRow("memory-length")]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task FilteredMinidumpRejectsMalformedStorage(string mutation)
    {
        string directory = Directory.CreateTempSubdirectory("csls-filtered-dump-").FullName;
        try
        {
            // These intentionally malformed files exercise the real parser's hostile-input boundary.
            byte[] file = new byte[100];
            "MDMP"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0xa793);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 32);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), 52);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), 44);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(72), 4096);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(80), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84), 96);
            switch (mutation)
            {
                case "header": file = file[..31]; break;
                case "version": file[4] = 0; break;
                case "streams": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 65537); break;
                case "directory": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), uint.MaxValue); break;
                case "no-threads": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 4); break;
                case "table-offset": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), uint.MaxValue); break;
                case "table-length": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), 3); break;
                case "thread-count": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 4097); break;
                case "thread-length": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 2); break;
                case "address-overflow": BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(72), ulong.MaxValue); break;
                case "memory-offset": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84), uint.MaxValue); break;
                case "memory-length": BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(80), 5); break;
                default: throw new AssertFailedException($"Unknown storage mutation {mutation}.");
            }
            string path = Path.Join(directory, "malformed.dmp");
            await File.WriteAllBytesAsync(path, file, TestContext.CancellationToken).ConfigureAwait(false);
            _ = Assert.ThrowsExactly<InvalidDataException>(() => DumpMemoryFilter.Read(path, TestContext.CancellationToken));
            using FileStream released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual(file.Length, released.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
