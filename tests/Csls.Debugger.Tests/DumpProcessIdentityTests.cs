using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies captured process identities against exited real targets and malformed copies of their dump records.
/// </summary>
[TestClass]
public sealed class DumpProcessIdentityTests : DapTestContext
{
    /// <summary>
    /// Preserves independently observed process identities in both triage and heap captures after target exit.
    /// </summary>
    /// <param name="includeHeap">Whether to include the target's managed heap.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedProcessIdentityMatchesExitedTarget(bool includeHeap)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, includeHeap: includeHeap).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        Assert.AreEqual(fixture.ProcessId,
            DumpProcessIdentity.Read(target.DataReader, fixture.DumpPath, TestContext.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
            DumpProcessIdentity.Read(target.DataReader, fixture.DumpPath, cancellation.Token));
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.AreEqual(fixture.ProcessId,
            DumpProcessIdentity.Read(target.DataReader, fixture.DumpPath, TestContext.CancellationToken));
    }

    /// <summary>
    /// Rejects malformed command and identity records without losing the original capture or caller-owned file.
    /// </summary>
    /// <param name="mutation">The independent Mach-O structural violation.</param>
    [TestMethod]
    [DataRow("magic")]
    [DataRow("file-type")]
    [DataRow("architecture")]
    [DataRow("command-count")]
    [DataRow("command-range")]
    [DataRow("command-size")]
    [DataRow("record-offset")]
    [DataRow("record-size")]
    [DataRow("pid-zero")]
    [DataRow("pid-overflow")]
    [DataRow("thread-count")]
    [OSCondition(OperatingSystems.OSX)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MachOIdentityRejectsMalformedRecords(string mutation)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string malformed = Path.ChangeExtension(fixture.DumpPath, ".malformed");
        File.Copy(fixture.DumpPath, malformed);
        (long command, long record) = LocateThreadInfo(malformed);
        using (FileStream input = File.Open(malformed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(input))
        {
            input.Position = mutation switch
            {
                "magic" => 0,
                "architecture" => 4,
                "file-type" => 12,
                "command-count" => 16,
                "command-range" => 20,
                "command-size" => command + 4,
                "record-offset" => command + 40,
                "record-size" => command + 48,
                "pid-zero" or "pid-overflow" => record + 16,
                "thread-count" => record + 20,
                _ => throw new AssertFailedException($"Unknown Mach-O record mutation '{mutation}'.")
            };
            switch (mutation)
            {
                case "record-offset":
                    writer.Write(ulong.MaxValue);
                    break;
                case "record-size":
                    writer.Write(23UL);
                    break;
                case "command-count":
                case "command-range":
                case "thread-count":
                    writer.Write(uint.MaxValue);
                    break;
                case "pid-overflow":
                    writer.Write((uint)int.MaxValue + 1);
                    break;
                default:
                    writer.Write(0U);
                    break;
            }
        }
        using (FileStream rejected = File.OpenRead(malformed))
        {
            _ = Assert.ThrowsExactly<InvalidDataException>(() =>
                DumpProcessIdentity.ReadMachO(rejected, TestContext.CancellationToken));
            Assert.IsTrue(rejected.CanRead);
        }
        using FileStream original = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(fixture.ProcessId, DumpProcessIdentity.ReadMachO(original, TestContext.CancellationToken));
        Assert.IsTrue(original.CanRead);
    }

    /// <summary>
    /// Reports an absent identity when a real capture contains no recognized CoreCLR thread-info signature.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.OSX)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MachOIdentityRequiresRecordedSignature()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        (_, long record) = LocateThreadInfo(fixture.DumpPath);
        using FileStream stream = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = record;
        stream.WriteByte(0);
        Assert.IsNull(DumpProcessIdentity.ReadMachO(stream, TestContext.CancellationToken));
        stream.Position = record;
        stream.WriteByte((byte)'T');
        Assert.AreEqual(fixture.ProcessId, DumpProcessIdentity.ReadMachO(stream, TestContext.CancellationToken));
    }

    private static (long Command, long Record) LocateThreadInfo(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.AreEqual(0xfeedfacfU, reader.ReadUInt32());
        stream.Position = 16;
        uint count = reader.ReadUInt32();
        stream.Position = 32;
        for (uint index = 0; index < count; index++)
        {
            long position = stream.Position;
            uint kind = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            Assert.IsGreaterThanOrEqualTo(8U, size);
            if (kind == 0x19)
            {
                stream.Position = position + 24;
                ulong address = reader.ReadUInt64();
                if (address is 0x7fffffff00000000 or 0x00007ffffff00000)
                {
                    stream.Position = position + 40;
                    return (position, checked((long)reader.ReadUInt64()));
                }
            }
            stream.Position = position + size;
        }
        throw new AssertFailedException("The real CoreCLR capture contains no thread-info segment.");
    }
}
