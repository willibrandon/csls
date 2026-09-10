using Csls.Debugger.Dump;
using Csls.Debugger.Interop;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises native captured-memory callbacks against independently created process dumps.
/// </summary>
[TestClass]
public sealed class DumpDataTargetTests : DapTestContext
{
    /// <summary>
    /// Reports absent memory distinctly and preserves valid native reads and caller-owned buffer boundaries.
    /// </summary>
    /// <param name="includeHeap">Whether the real dump includes the managed heap.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MissingMemoryReadPreservesBufferAndSubsequentReads(bool includeHeap)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, includeHeap: includeHeap).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        ClrInfo runtime = Assert.ContainsSingle(target.ClrVersions);
        using var callbacks = new CorDebugDumpCallbacks(new DumpCorDebugSource(runtime, null))
        {
            Operation = new CorDebugDumpReadOperation(TestContext.CancellationToken, null)
        };
        byte[] buffer = new byte[17];
        Array.Fill(buffer, (byte)0xA5);
        Assert.AreEqual(0, target.DataReader.Read(0, buffer));

        (int Result, uint Read) missing = Read(callbacks, 0, buffer, 16);
        Assert.AreEqual(unchecked((int)0x80131C49), missing.Result);
        Assert.AreEqual(0U, missing.Read);
        Assert.AreSequenceEqual(Enumerable.Repeat((byte)0xA5, buffer.Length), buffer);
        Assert.AreEqual(1L, callbacks.MissingMemoryReads);
        Assert.IsNull(callbacks.LastFailure);

        (int Result, uint Read) empty = Read(callbacks, 0, buffer, 0);
        Assert.AreEqual(0, empty.Result);
        Assert.AreEqual(0U, empty.Read);
        Assert.AreSequenceEqual(Enumerable.Repeat((byte)0xA5, buffer.Length), buffer);
        Assert.AreEqual(1L, callbacks.MissingMemoryReads);

        byte[] expected = new byte[16];
        string runtimeImage = Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), Path.GetFileName(runtime.ModuleInfo.FileName));
        using (FileStream image = File.OpenRead(runtimeImage))
        {
            await image.ReadExactlyAsync(expected, TestContext.CancellationToken).ConfigureAwait(false);
        }
        (int Result, uint Read) captured = Read(callbacks, runtime.ModuleInfo.ImageBase, buffer, 16);
        Assert.AreEqual(0, captured.Result);
        Assert.AreEqual(16U, captured.Read);
        Assert.AreSequenceEqual(expected, buffer.Take(16));
        Assert.AreEqual((byte)0xA5, buffer[16]);
        Assert.AreEqual(1L, callbacks.MissingMemoryReads);

        callbacks.Operation = new CorDebugDumpReadOperation(TestContext.CancellationToken, null);
        Assert.AreEqual(0L, callbacks.MissingMemoryReads);
        Assert.IsNull(callbacks.LastFailure);
    }

    private static unsafe (int Result, uint Read) Read(CorDebugDumpCallbacks callbacks,
        ulong address, byte[] buffer, uint requested)
    {
        nint target = (nint)ComInterfaceMarshaller<ICorDebugDumpDataTarget>.ConvertToUnmanaged(callbacks);
        try
        {
            uint read = uint.MaxValue;
            int result;
            fixed (byte* destination = buffer)
            {
                result = new ICorDebugDataTargetAbi(target).ReadVirtual(address, (nint)destination,
                    requested, (nint)(&read));
            }
            return (result, Volatile.Read(ref read));
        }
        finally
        {
            _ = ComAbi.Release(target);
            GC.KeepAlive(callbacks);
        }
    }
}
