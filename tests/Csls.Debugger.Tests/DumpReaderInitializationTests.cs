using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises captured-thread initialization and file ownership through real dump readers.
/// </summary>
[TestClass]
public sealed class DumpReaderInitializationTests : DapTestContext
{
    /// <summary>
    /// Observes malformed thread metadata during opening and releases the rejected file immediately.
    /// </summary>
    /// <param name="streamKind">The native thread-table stream containing an excessive count.</param>
    /// <param name="lockFree">Whether captured memory uses the lock-free mapped reader.</param>
    [TestMethod]
    [DataRow(3U, false)]
    [DataRow(3U, true)]
    [DataRow(8U, false)]
    [DataRow(8U, true)]
    [DataRow(17U, false)]
    [DataRow(17U, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MalformedThreadTableFailsBeforeOwnershipTransfer(uint streamKind, bool lockFree)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateHostileMinidump(streamKind), TestContext.CancellationToken)
                .ConfigureAwait(false);
            var options = new DataTargetOptions { SymbolPaths = [], UseLockFreeMemoryMapReader = lockFree };
            InvalidDataException failure = Assert.ThrowsExactly<InvalidDataException>(() =>
            {
                using DataTarget target = DumpMachODataReader.Open(path, options, TestContext.CancellationToken);
            });
            Assert.Contains("maximum", failure.Message);
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
            using FileStream released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual(140L, released.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Keeps the session reusable when native thread parsing fails before managed runtime selection.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MalformedThreadTablePreservesSessionAndAllowsValidOpen()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string path = Path.Join(Path.GetDirectoryName(fixture.DumpPath), "invalid-threads.dmp");
        await File.WriteAllBytesAsync(path, CreateHostileMinidump(3), TestContext.CancellationToken)
            .ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        InvalidDataException failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.OpenDumpAsync(new DebugDumpOpenRequest(path, RuntimeIndex: int.MaxValue),
                TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.Contains("maximum", failure.Message);
        DebugSessionSnapshot rejected = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, rejected.State);
        Assert.AreEqual(0L, rejected.StopGeneration);
        using (FileStream released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(140L, released.Length);
        }

        DebugSessionSnapshot recovered = await service.OpenDumpAsync(fixture.OpenRequest,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, recovered.State);
        Assert.AreEqual(fixture.ProcessId, recovered.ProcessId);
        Assert.IsNotEmpty(await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream closed = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, closed.Length);
    }

    /// <summary>
    /// Preserves captured process identity and native threads with both mapped-reader policies.
    /// </summary>
    /// <param name="lockFree">Whether captured memory uses the lock-free mapped reader.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedThreadsRemainReadableUntilDisposal(bool lockFree)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var options = new DataTargetOptions { SymbolPaths = [], UseLockFreeMemoryMapReader = lockFree };
        using (DataTarget target = DumpMachODataReader.Open(fixture.DumpPath, options, TestContext.CancellationToken))
        {
            Assert.AreEqual(fixture.ProcessId,
                DumpProcessIdentity.Read(target.DataReader, fixture.DumpPath, TestContext.CancellationToken));
            IThreadReader reader = Assert.IsInstanceOfType<IThreadReader>(target.DataReader);
            uint[] threads = [.. reader.EnumerateOSThreadIds()];
            Assert.IsNotEmpty(threads);
            Assert.AreSequenceEqual(threads, reader.EnumerateOSThreadIds());
            _ = Assert.ContainsSingle(target.ClrVersions);
        }
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Honors cancellation before opening a file and preserves the caller's cancellation identity.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PreCanceledOpenLeavesFileAvailable()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateHostileMinidump(3), TestContext.CancellationToken)
                .ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);
            using FileStream held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
            {
                using DataTarget target = DumpMachODataReader.Open(path, new DataTargetOptions { SymbolPaths = [] },
                    cancellation.Token);
            });
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            Assert.AreEqual(140L, held.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateHostileMinidump(uint streamKind)
    {
        // The genuine parser receives a minidump with an oversized native thread count through a file.
        byte[] bytes = new byte[140];
        "MDMP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xa793);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 32);
        WriteDirectory(bytes.AsSpan(32), 7, 56, 68);
        WriteDirectory(bytes.AsSpan(44), 4, 4, 124);
        WriteDirectory(bytes.AsSpan(56), streamKind, streamKind == 17 ? 12U : 4U, 128);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 9);
        uint excessive = checked((uint)new DataTargetLimits().MaxThreads + 1);
        if (streamKind == 17)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136), excessive);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), excessive);
        }
        return bytes;
    }

    private static void WriteDirectory(Span<byte> entry, uint kind, uint size, uint offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry, kind);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], offset);
    }
}
