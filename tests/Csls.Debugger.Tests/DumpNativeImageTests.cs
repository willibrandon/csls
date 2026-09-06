using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native dump-library validation through real captured dumps and hostile executable files.
/// </summary>
[TestClass]
public sealed class DumpNativeImageTests : DapTestContext
{
    /// <summary>
    /// Rejects malformed or mismatched ELF DACs before activation and preserves subsequent valid inspection.
    /// </summary>
    /// <param name="mutation">The independent ELF identity or layout violation.</param>
    [TestMethod]
    [DataRow("runtime")]
    [DataRow("truncated")]
    [DataRow("architecture")]
    [DataRow("class")]
    [DataRow("endianness")]
    [DataRow("program-table-offset")]
    [DataRow("program-table-count")]
    [DataRow("image-content")]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ElfDacRequiresExactIdentityAndRecovers(string mutation)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string correct = Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), "libmscordaccore.so");
        string wrong = Path.ChangeExtension(fixture.DumpPath, ".so");
        byte[] bytes = await File.ReadAllBytesAsync(mutation == "runtime"
            ? Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), "libcoreclr.so") : correct,
            TestContext.CancellationToken).ConfigureAwait(false);
        switch (mutation)
        {
            case "runtime":
                break;
            case "truncated":
                bytes = bytes[..32];
                break;
            case "architecture":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 0);
                break;
            case "class":
                bytes[4] = 1;
                break;
            case "endianness":
                bytes[5] = 2;
                break;
            case "program-table-offset":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue);
                break;
            case "program-table-count":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), ushort.MaxValue);
                break;
            case "image-content":
                bytes[^1] ^= 1;
                break;
            default:
                Assert.Fail($"Unknown ELF mutation {mutation}.");
                break;
        }

        await File.WriteAllBytesAsync(wrong, bytes, TestContext.CancellationToken).ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        InvalidDataException failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenDumpAsync(
            new DebugDumpOpenRequest(fixture.DumpPath, DacPath: wrong), TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.Contains("debugging-library identity", failure.Message);
        DebugSessionSnapshot rejected = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, rejected.State);
        using (FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsGreaterThan(0L, released.Length);
        }

        DebugSessionSnapshot opened = await service.OpenDumpAsync(new DebugDumpOpenRequest(fixture.DumpPath, DacPath: correct),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, opened.State);
        Assert.AreEqual(fixture.ProcessId, opened.ProcessId);
        DebugModulePage modules = await service.GetModulesAsync(new DebugModulesRequest(0, 0),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("csls-test-process-host.dll", modules.Modules.Select(module => module.Name));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }
}
