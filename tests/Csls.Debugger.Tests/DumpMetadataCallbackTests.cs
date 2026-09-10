using Csls.Debugger.Dump;
using Csls.Debugger.Interop;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises the native metadata callback with real captured runtimes and independently validated module files.
/// </summary>
[TestClass]
public sealed class DumpMetadataCallbackTests : DapTestContext
{
    /// <summary>
    /// Finds relocated captured binaries through immutable explicit roots and skips a mismatched earlier candidate.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackResolvesRelocatedExactImage()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string original = Path.GetDirectoryName(fixture.ProgramPath)
            ?? throw new InvalidOperationException("The fixture has no directory.");
        string relocated = original + "-relocated";
        Directory.Move(original, relocated);
        string wrong = Directory.CreateDirectory(original + "-wrong").FullName;
        string name = Path.GetFileName(fixture.ProgramPath);
        string image = Path.Join(relocated, name);
        File.Copy(typeof(DumpMetadataCallbackTests).Assembly.Location, Path.Join(wrong, name));
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        ClrInfo runtime = Assert.ContainsSingle(target.ClrVersions);
        (uint timestamp, uint size) = ReadIdentity(image);
        using (var missing = new CorDebugDumpCallbacks(new DumpCorDebugSource(runtime, null, [wrong])))
        {
            (int Result, uint Length, string Path) rejected = Request(missing, name, timestamp, size, 32768);
            Assert.AreEqual(unchecked((int)0x80004005), rejected.Result);
            Assert.AreEqual(0U, rejected.Length);
            Assert.AreEqual(string.Empty, rejected.Path);
        }

        string[] paths = [wrong, relocated];
        var source = new DumpCorDebugSource(runtime, null, paths);
        paths[1] = wrong;
        using var callbacks = new CorDebugDumpCallbacks(source);
        (int Result, uint Length, string Path) resolved = Request(callbacks, name, timestamp, size, 32768);
        Assert.AreEqual(0, resolved.Result, callbacks.LastFailure?.ToString());
        Assert.AreEqual((timestamp, size), ReadIdentity(resolved.Path));
        Assert.AreSequenceEqual(
            SHA256.HashData(await File.ReadAllBytesAsync(image, TestContext.CancellationToken).ConfigureAwait(false)),
            SHA256.HashData(await File.ReadAllBytesAsync(resolved.Path, TestContext.CancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// Resolves a basename-only runtime request through the module paths recorded in the captured target.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackResolvesCapturedModuleBasename()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        ClrInfo info = Assert.ContainsSingle(target.ClrVersions);
        using ClrRuntime runtime = info.CreateRuntime(DumpDacResolver.Resolve(info, null), ignoreMismatch: info.Version.Major == 0);
        var source = new DumpCorDebugSource(info, null,
            [Path.GetDirectoryName(fixture.ProgramPath) ?? throw new InvalidOperationException("The fixture has no directory.")]);
        using var callbacks = new CorDebugDumpCallbacks(source);
        string name = Path.GetFileName(fixture.ProgramPath);
        Assert.Contains(name, runtime.EnumerateModules().Select(module => Path.GetFileName(module.Name)));
        (uint timestamp, uint size) = ReadIdentity(fixture.ProgramPath);
        (int Result, uint Length, string Path) resolved = Request(callbacks, name, timestamp, size, 32768);
        Assert.AreEqual(0, resolved.Result, $"{callbacks.LastFailure}; candidates: {string.Join(", ", source.FindMetadataImages(name).Take(8))}");
        Assert.AreEqual((timestamp, size), ReadIdentity(resolved.Path));
        Assert.AreSequenceEqual(
            SHA256.HashData(await File.ReadAllBytesAsync(fixture.ProgramPath, TestContext.CancellationToken).ConfigureAwait(false)),
            SHA256.HashData(await File.ReadAllBytesAsync(resolved.Path, TestContext.CancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// Retains an exact private image across buffer sizing, original-file replacement, and native requests.
    /// </summary>
    /// <param name="runtimeImage">Whether to request CoreLib rather than the AnyCPU application module.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackRetainsExactImage(bool runtimeImage)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        using var callbacks = new CorDebugDumpCallbacks(source);
        string image = Path.Join(Path.GetDirectoryName(fixture.ProgramPath), "métadonnées-λ.dll");
        File.Copy(runtimeImage ? typeof(object).Assembly.Location : fixture.ProgramPath, image);
        (uint timestamp, uint size) = ReadIdentity(image);
        byte[] expected = SHA256.HashData(await File.ReadAllBytesAsync(image, TestContext.CancellationToken).ConfigureAwait(false));

        (int Result, uint Length, string Path) shortBuffer = Request(callbacks, image, timestamp, size, 1);
        Assert.AreEqual(unchecked((int)0x8007007A), shortBuffer.Result);
        Assert.IsGreaterThan(1U, shortBuffer.Length);
        Assert.AreEqual(string.Empty, shortBuffer.Path);
        (int Result, uint Length, string Path) resolved = Request(callbacks, image, timestamp, size, shortBuffer.Length);
        Assert.AreEqual(0, resolved.Result);
        Assert.AreEqual(shortBuffer.Length, resolved.Length);
        Assert.AreNotEqual(image, resolved.Path);
        Assert.IsTrue(Path.IsPathFullyQualified(resolved.Path));
        Assert.AreEqual((timestamp, size), ReadIdentity(resolved.Path));
        Assert.AreSequenceEqual(expected,
            SHA256.HashData(await File.ReadAllBytesAsync(resolved.Path, TestContext.CancellationToken).ConfigureAwait(false)));

        File.Copy(typeof(DumpMetadataCallbackTests).Assembly.Location, image, overwrite: true);
        (int Result, uint Length, string Path) repeated = Request(callbacks, image, timestamp, size, resolved.Length);
        Assert.AreEqual(0, repeated.Result);
        Assert.AreEqual(resolved.Path, repeated.Path);
        Assert.AreSequenceEqual(expected,
            SHA256.HashData(await File.ReadAllBytesAsync(repeated.Path, TestContext.CancellationToken).ConfigureAwait(false)));
        callbacks.Dispose();
        Assert.IsFalse(File.Exists(resolved.Path));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(resolved.Path)));
        (int Result, uint Length, string Path) disposed = Request(callbacks, image, timestamp, size, resolved.Length);
        Assert.AreEqual(unchecked((int)0x80004005), disposed.Result);
        Assert.AreEqual(0U, disposed.Length);
        using FileStream released = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Rejects independently corrupted image identities and recovers with the original exact image.
    /// </summary>
    /// <param name="change">The untrusted file or requested-identity mutation.</param>
    [TestMethod]
    [DataRow("timestamp")]
    [DataRow("image-size")]
    [DataRow("empty")]
    [DataRow("truncated")]
    [DataRow("different")]
    [DataRow("oversized")]
    [DataRow("metadata-size")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackRejectsMismatchedImagesAndRecovers(string change)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        using var callbacks = new CorDebugDumpCallbacks(source);
        string image = Path.Join(Path.GetDirectoryName(fixture.ProgramPath), "requested-metadata.dll");
        File.Copy(fixture.ProgramPath, image);
        (uint timestamp, uint size) = ReadIdentity(image);
        switch (change)
        {
            case "timestamp":
                timestamp ^= 1;
                break;
            case "image-size":
                size ^= 1;
                break;
            case "empty":
            case "truncated":
            case "oversized":
                using (FileStream stream = File.Open(image, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(change switch { "empty" => 0, "oversized" => 512L * 1024 * 1024 + 1, _ => 64 });
                }
                break;
            case "metadata-size":
                using (FileStream stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var reader = new PEReader(stream, PEStreamOptions.LeaveOpen))
                using (var writer = new BinaryWriter(stream))
                {
                    stream.Position = reader.PEHeaders.CorHeaderStartOffset + 12;
                    writer.Write(64 * 1024 * 1024 + 1);
                }
                break;
            case "different":
                File.Copy(typeof(DumpMetadataCallbackTests).Assembly.Location, image, overwrite: true);
                break;
            default:
                Assert.Fail($"Unknown metadata image mutation '{change}'.");
                break;
        }

        (int Result, uint Length, string Path) rejected = Request(callbacks, image, timestamp, size, 32768);
        Assert.AreEqual(unchecked((int)0x80004005), rejected.Result);
        Assert.AreEqual(0U, rejected.Length);
        Assert.AreEqual(string.Empty, rejected.Path);
        Assert.IsNotNull(callbacks.LastFailure);
        callbacks.Operation = new CorDebugDumpReadOperation(TestContext.CancellationToken, null);
        Assert.IsNull(callbacks.LastFailure);
        File.Copy(fixture.ProgramPath, image, overwrite: true);
        (timestamp, size) = ReadIdentity(image);
        (int Result, uint Length, string Path) recovered = Request(callbacks, image, timestamp, size, 32768);
        Assert.AreEqual(0, recovered.Result);
        Assert.AreEqual((timestamp, size), ReadIdentity(recovered.Path));
    }

    /// <summary>
    /// Cancels native metadata requests before file discovery and preserves later exact-image resolution.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CanceledMetadataCallbackPreservesSession()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        using var callbacks = new CorDebugDumpCallbacks(source);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        callbacks.Operation = new CorDebugDumpReadOperation(cancellation.Token, null);
        (uint timestamp, uint size) = ReadIdentity(fixture.ProgramPath);
        (int Result, uint Length, string Path) canceled = Request(callbacks, fixture.ProgramPath, timestamp, size, 32768);
        Assert.AreEqual(unchecked((int)0x80004004), canceled.Result);
        Assert.AreEqual(0U, canceled.Length);
        Assert.AreEqual(string.Empty, canceled.Path);
        callbacks.Operation = null;
        (int Result, uint Length, string Path) resolved = Request(callbacks, fixture.ProgramPath, timestamp, size, 32768);
        Assert.AreEqual(0, resolved.Result);
        Assert.AreEqual((timestamp, size), ReadIdentity(resolved.Path));
    }

    /// <summary>
    /// Preserves caller-owned buffers when the native request has invalid or insufficient path storage.
    /// </summary>
    /// <param name="input">The requested path shape.</param>
    /// <param name="capacity">The caller's declared buffer capacity.</param>
    /// <param name="expected">The expected native HRESULT.</param>
    [TestMethod]
    [DataRow("matching", 0U, unchecked((int)0x8007007A))]
    [DataRow("matching", 32769U, unchecked((int)0x80070057))]
    [DataRow("null", 32768U, unchecked((int)0x80070057))]
    [DataRow("empty", 32768U, unchecked((int)0x80004005))]
    [DataRow("oversized", 32768U, unchecked((int)0x80004005))]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackValidatesNativeBuffer(string input, uint capacity, int expected)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        using var callbacks = new CorDebugDumpCallbacks(source);
        (uint timestamp, uint size) = ReadIdentity(fixture.ProgramPath);
        string? image = input switch
        {
            "matching" => fixture.ProgramPath,
            "null" => null,
            "empty" => string.Empty,
            "oversized" => new string('a', 32768),
            _ => throw new AssertFailedException($"Unknown native request input '{input}'.")
        };
        (int Result, uint Length, string Path) rejected = Request(callbacks, image, timestamp, size, capacity);
        Assert.AreEqual(expected, rejected.Result);
        Assert.AreEqual(string.Empty, rejected.Path);
        if (capacity == 0)
        {
            Assert.IsGreaterThan(1U, rejected.Length);
        }
        else
        {
            Assert.AreEqual(0U, rejected.Length);
        }
        (int Result, uint Length, string Path) recovered = Request(callbacks, fixture.ProgramPath, timestamp, size, 32768);
        Assert.AreEqual(0, recovered.Result);
        Assert.AreEqual((timestamp, size), ReadIdentity(recovered.Path));
    }

    /// <summary>
    /// Rejects a real unconnected FIFO without waiting for another process to open the write side.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MetadataCallbackRejectsUnconnectedFifo()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        using var callbacks = new CorDebugDumpCallbacks(source);
        string image = Path.Join(Path.GetDirectoryName(fixture.ProgramPath), "unconnected-metadata.dll");
        var command = new ProcessStartInfo("mkfifo") { ArgumentList = { image } };
        using (Process process = Process.Start(command) ?? throw new InvalidOperationException("mkfifo did not start."))
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode);
        }
        (uint timestamp, uint size) = ReadIdentity(fixture.ProgramPath);
        (int Result, uint Length, string Path) rejected = Request(callbacks, image, timestamp, size, 32768);
        Assert.AreEqual(unchecked((int)0x80004005), rejected.Result);
        Assert.AreEqual(0U, rejected.Length);
        (int Result, uint Length, string Path) recovered = Request(callbacks, fixture.ProgramPath, timestamp, size, 32768);
        Assert.AreEqual(0, recovered.Result);
        Assert.AreEqual((timestamp, size), ReadIdentity(recovered.Path));
    }

    private static (uint Timestamp, uint Size) ReadIdentity(string path)
    {
        using FileStream image = File.OpenRead(path);
        using var pe = new PEReader(image);
        Assert.IsTrue(pe.HasMetadata);
        PEHeader header = Assert.IsInstanceOfType<PEHeader>(pe.PEHeaders.PEHeader);
        return (unchecked((uint)pe.PEHeaders.CoffHeader.TimeDateStamp), checked((uint)header.SizeOfImage));
    }

    private static unsafe (int Result, uint Length, string Path) Request(CorDebugDumpCallbacks callbacks,
        string? image, uint timestamp, uint size, uint capacity)
    {
        char[] buffer = new char[checked((int)capacity + 1)];
        Array.Fill(buffer, '\u25a1');
        nint target = (nint)ComInterfaceMarshaller<ICorDebugDumpDataTarget>.ConvertToUnmanaged(callbacks);
        try
        {
            nint locator = ComAbi.QueryInterface(target, ICorDebugMetaDataLocatorAbi.InterfaceId);
            try
            {
                uint length = 0;
                int result;
                fixed (char* input = image)
                fixed (char* output = buffer)
                {
                    result = new ICorDebugMetaDataLocatorAbi(locator).GetMetaData((nint)input, timestamp, size,
                        capacity, (nint)(&length), (nint)output);
                }
                length = Volatile.Read(ref length);
                Assert.AreEqual('\u25a1', buffer[^1]);
                if (result != 0)
                {
                    Assert.IsTrue(buffer.All(static character => character == '\u25a1'));
                    return (result, length, string.Empty);
                }

                Assert.IsGreaterThan(0U, length);
                Assert.IsLessThanOrEqualTo(capacity, length);
                Assert.AreEqual('\0', buffer[length - 1]);
                return (result, length, new string(buffer, 0, checked((int)length - 1)));
            }
            finally
            {
                _ = ComAbi.Release(locator);
            }
        }
        finally
        {
            _ = ComAbi.Release(target);
            GC.KeepAlive(callbacks);
        }
    }
}
