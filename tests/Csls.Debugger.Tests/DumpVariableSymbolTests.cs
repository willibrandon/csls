using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies captured frame names against real module and symbol files changed after target exit.
/// </summary>
[TestClass]
public sealed class DumpVariableSymbolTests : DapTestContext
{
    /// <summary>
    /// Uses captured parameter metadata and accepts local names only from the captured symbol identity.
    /// </summary>
    /// <param name="change">The isolated on-disk image or PDB change after capture.</param>
    /// <param name="hasLocalNames">Whether the remaining PDB still matches the captured image.</param>
    [TestMethod]
    [DataRow("matching", true)]
    [DataRow("module", true)]
    [DataRow("missing", false)]
    [DataRow("different", false)]
    [DataRow("truncated", false)]
    [DataRow("guid", false)]
    [DataRow("stamp", false)]
    [DataRow("sequence-points", true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedNamesRequireOriginalSymbols(string change, bool hasLocalNames)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, isolateModule: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string pdbPath = Path.ChangeExtension(fixture.ProgramPath, ".pdb");
        string otherModule = typeof(DumpVariableSymbolTests).Assembly.Location;
        switch (change)
        {
            case "matching":
                break;
            case "module":
                File.Copy(otherModule, fixture.ProgramPath, overwrite: true);
                break;
            case "missing":
                File.Move(pdbPath, pdbPath + ".saved");
                break;
            case "different":
                File.Copy(otherModule, fixture.ProgramPath, overwrite: true);
                File.Copy(Path.ChangeExtension(otherModule, ".pdb"), pdbPath, overwrite: true);
                break;
            case "truncated":
                using (FileStream file = File.Open(pdbPath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    file.SetLength(32);
                }
                break;
            case "guid":
            case "stamp":
                byte[] image = await File.ReadAllBytesAsync(pdbPath, TestContext.CancellationToken).ConfigureAwait(false);
                using (var provider = MetadataReaderProvider.FromPortablePdbImage([.. image]))
                {
                    DebugMetadataHeader header = provider.GetMetadataReader().DebugMetadataHeader
                        ?? throw new AssertFailedException("The real fixture must contain a Portable PDB identity.");
                    int idOffset = image.AsSpan().IndexOf(header.Id.AsSpan());
                    Assert.IsGreaterThanOrEqualTo(0, idOffset);
                    image[idOffset + (change == "guid" ? 0 : 16)] ^= 1;
                }
                await File.WriteAllBytesAsync(pdbPath, image, TestContext.CancellationToken).ConfigureAwait(false);
                break;
            case "sequence-points":
                byte[] damaged = await File.ReadAllBytesAsync(pdbPath, TestContext.CancellationToken).ConfigureAwait(false);
                using (FileStream stream = File.OpenRead(fixture.ProgramPath))
                using (var pe = new PEReader(stream))
                using (var provider = MetadataReaderProvider.FromPortablePdbImage([.. damaged]))
                {
                    MetadataReader metadata = pe.GetMetadataReader();
                    MethodDefinitionHandle method = Assert.ContainsSingle(metadata.MethodDefinitions.Where(handle =>
                    {
                        MethodDefinition definition = metadata.GetMethodDefinition(handle);
                        return metadata.GetString(definition.Name) == "WaitForSignal" &&
                            metadata.GetString(metadata.GetTypeDefinition(definition.GetDeclaringType()).Name) == "DebuggerFixture";
                    }));
                    MetadataReader symbols = provider.GetMetadataReader();
                    BlobHandle sequencePoints = symbols.GetMethodDebugInformation(method.ToDebugInformationHandle()).SequencePointsBlob;
                    int offset = symbols.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(sequencePoints);
                    int prefixLength = (damaged[offset] & 0x80) == 0 ? 1 : (damaged[offset] & 0x40) == 0 ? 2 : 4;
                    Assert.IsGreaterThan(0, symbols.GetBlobBytes(sequencePoints).Length);
                    damaged[offset + prefixLength] = 0xff;
                }
                await File.WriteAllBytesAsync(pdbPath, damaged, TestContext.CancellationToken).ConfigureAwait(false);
                break;
            default:
                Assert.Fail($"Unknown isolated symbol change {change}.");
                break;
        }

        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(new DebugDumpOpenRequest(fixture.DumpPath), TestContext.CancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DebugThreadInfo> threads = await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        List<DebugStackFrameInfo> frames = [];
        foreach (DebugThreadInfo thread in threads)
        {
            DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(thread.Id, 0, 0),
                TestContext.CancellationToken).ConfigureAwait(false);
            frames.AddRange(stack.StackFrames);
        }

        DebugStackFrameInfo selected = Assert.ContainsSingle(frames.Where(frame =>
            frame.Name.Contains("DebuggerFixture.WaitForSignal", StringComparison.Ordinal)));
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(selected.Id),
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugScopeInfo arguments = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Arguments"));
        DebugScopeInfo locals = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
        IReadOnlyList<DebugVariableInfo> argumentValues = await service.GetVariablesAsync(
            new DebugVariablesRequest(arguments.VariablesReference, 2, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, argumentValues);
        Assert.AreEqual("number", argumentValues[0].Name);
        Assert.AreEqual("text", argumentValues[1].Name);
        Assert.AreEqual("42", argumentValues[0].Value);
        Assert.AreEqual("int", argumentValues[0].Type);
        IReadOnlyList<DebugVariableInfo> localValues = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, localValues);
        Assert.AreEqual(hasLocalNames ? "localNumber" : "local 0", localValues[0].Name);
        Assert.AreEqual(hasLocalNames ? "localLong" : "local 1", localValues[1].Name);
        Assert.AreEqual("43", localValues[0].Value);
        Assert.AreEqual("44", localValues[1].Value);
        IReadOnlyList<DebugVariableInfo> page = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 1, 1, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(localValues[1], Assert.ContainsSingle(page));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream releasedDump = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream releasedModule = File.Open(fixture.ProgramPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, releasedDump.Length);
        Assert.IsGreaterThan(0L, releasedModule.Length);
        if (change != "missing")
        {
            using FileStream releasedSymbols = File.Open(pdbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsGreaterThan(0L, releasedSymbols.Length);
        }
    }
}
