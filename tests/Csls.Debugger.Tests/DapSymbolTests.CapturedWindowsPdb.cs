using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies captured module identity and resource ownership with native Windows symbols.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Uses the captured image to match Windows symbols after files change or move.
    /// </summary>
    /// <param name="change">The isolated module or symbol change after capturing the image.</param>
    /// <param name="hasNames">Whether identity-matched local names remain available.</param>
    [TestMethod]
    [DataRow("matching", true)]
    [DataRow("module", true)]
    [DataRow("missing", false)]
    [DataRow("different", false)]
    [DataRow("guid", false)]
    [DataRow("age", false)]
    [DataRow("relocated", true)]
    [DataRow("below-limit", true)]
    [DataRow("at-limit", true)]
    [DataRow("oversized", false)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task CapturedWindowsPdbNamesUseOriginalIdentity(string change, bool hasNames)
    {
        string original = SymbolFixtures.WindowsPdbProgramPath
            ?? throw new AssertFailedException("The Windows symbol fixture is missing.");
        string directory = Directory.CreateTempSubdirectory("csls-captured-windows-pdb-").FullName;
        try
        {
            string program = Path.Join(directory, Path.GetFileName(original));
            string pdb = Path.ChangeExtension(program, ".pdb");
            string capture = Path.Join(directory, "captured.dll");
            File.Copy(original, capture);
            File.Copy(original, program);
            File.Copy(Path.ChangeExtension(original, ".pdb"), pdb);
            uint token;
            using (FileStream image = File.OpenRead(capture))
            using (var pe = new PEReader(image))
            {
                token = checked((uint)(pe.PEHeaders.CorHeader
                    ?? throw new AssertFailedException("The fixture has no managed entry point."))
                    .EntryPointTokenOrRelativeVirtualAddress);
            }

            IReadOnlyList<string> searchPaths = [];
            switch (change)
            {
                case "matching":
                    break;
                case "module":
                    File.Copy(SymbolFixtures.SymbolFreeProgramPath, program, overwrite: true);
                    break;
                case "missing":
                    File.Move(pdb, pdb + ".saved");
                    break;
                case "different":
                    File.Copy(typeof(DapSymbolTests).Assembly.Location, program, overwrite: true);
                    File.Copy(Path.ChangeExtension(typeof(DapSymbolTests).Assembly.Location, ".pdb"), pdb, overwrite: true);
                    break;
                case "relocated":
                    string relocated = Directory.CreateDirectory(Path.Join(directory, "symbols")).FullName;
                    File.Move(pdb, Path.Join(relocated, Path.GetFileName(pdb)));
                    File.Delete(program);
                    searchPaths = [relocated];
                    break;
                case "guid":
                case "age":
                    using (FileStream image = File.Open(capture, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    using (var pe = new PEReader(image, PEStreamOptions.LeaveOpen))
                    {
                        DebugDirectoryEntry codeView = Assert.ContainsSingle(pe.ReadDebugDirectory()
                            .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView));
                        long offset = codeView.DataPointer + (change == "guid" ? 4 : 20);
                        image.Position = offset;
                        int originalByte = image.ReadByte();
                        Assert.IsGreaterThanOrEqualTo(0, originalByte);
                        image.Position = offset;
                        image.WriteByte(checked((byte)(originalByte ^ 1)));
                    }
                    break;
                case "below-limit":
                case "at-limit":
                case "oversized":
                    using (FileStream symbols = File.Open(pdb, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        symbols.SetLength(256L * 1024 * 1024 + (change switch
                        {
                            "below-limit" => -1,
                            "oversized" => 1,
                            _ => 0
                        }));
                    }
                    break;
                default:
                    Assert.Fail($"Unknown symbol change {change}.");
                    break;
            }

            using (FileStream image = File.OpenRead(capture))
            {
                IReadOnlyDictionary<int, string> locals = CapturedModuleVariableNames.Read(
                    image, false, program, token, 0, arguments: false, searchPaths);
                if (hasNames)
                {
                    Assert.AreEqual("answer", locals[0]);
                    Assert.AreEqual("value", locals[1]);
                }
                else
                {
                    Assert.IsEmpty(locals);
                }
                image.Position = 0;
                IReadOnlyDictionary<int, string> arguments = CapturedModuleVariableNames.Read(
                    image, false, program, token, 0, arguments: true, searchPaths);
                Assert.AreEqual("arguments", Assert.ContainsSingle(arguments).Value);
                Assert.IsTrue(image.CanRead, "The captured image remains caller-owned.");
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                using FileStream released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.IsGreaterThan(0L, released.Length, path);
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps Windows symbol metadata valid after disposing the caller-owned PE and stream.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task CapturedWindowsPdbOwnsMetadataSnapshot()
    {
        string original = SymbolFixtures.WindowsPdbProgramPath
            ?? throw new AssertFailedException("The Windows symbol fixture is missing.");
        string directory = Directory.CreateTempSubdirectory("csls-captured-windows-pdb-").FullName;
        try
        {
            string program = Path.Join(directory, Path.GetFileName(original));
            string pdb = Path.ChangeExtension(program, ".pdb");
            File.Copy(original, program);
            File.Copy(Path.ChangeExtension(original, ".pdb"), pdb);
            using (var owner = new DisposableOwner<DebugSymbolReader>())
            {
                uint token;
                using (FileStream image = File.OpenRead(program))
                using (var pe = new PEReader(image))
                {
                    token = checked((uint)(pe.PEHeaders.CorHeader
                        ?? throw new AssertFailedException("The fixture has no managed entry point."))
                        .EntryPointTokenOrRelativeVirtualAddress);
                    owner.Acquire(() => DebugSymbolReader.TryOpen(pe, program));
                }
                DebugSymbolReader reader = owner.Value
                    ?? throw new AssertFailedException("The captured image's Windows symbols must open.");
                Assert.AreEqual(DebugSymbolStorageKind.Windows, reader.StorageKind);
                using FileStream released = File.Open(program, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.IsGreaterThan(0L, released.Length);
                Assert.AreEqual("answer", reader.GetLocalVariables(token, 0)[0].Name);
                Assert.IsNotEmpty(reader.GetSequencePoints(null));
            }
            using FileStream releasedSymbols = File.Open(pdb, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsGreaterThan(0L, releasedSymbols.Length);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads named Windows-PDB locals from a real dump after the independently running target exits.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task WindowsPdbDumpPreservesCapturedLocalNames()
    {
        string program = SymbolFixtures.WindowsPdbProgramPath
            ?? throw new AssertFailedException("The Windows symbol fixture is missing.");
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(program,
            TestContext.CancellationToken, captureType: DumpType.Full, diagnosticContext: TestContext,
            arguments: [string.Empty, "41", "ready"]).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        try
        {
            _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<DebugThreadInfo> threads = await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false);
            List<DebugStackFrameInfo> frames = [];
            foreach (DebugThreadInfo thread in threads)
            {
                DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(thread.Id, 0, 0),
                    TestContext.CancellationToken).ConfigureAwait(false);
                frames.AddRange(stack.StackFrames);
            }
            DebugStackFrameInfo main = Assert.ContainsSingle(frames.Where(frame =>
                frame.Name.Contains("Csls.Debugger.Fixtures.CSharp.Program.Main", StringComparison.Ordinal)));
            IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(main.Id),
                TestContext.CancellationToken).ConfigureAwait(false);
            DebugScopeInfo locals = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
            DebugVariableInfo answer = Assert.ContainsSingle(await service.GetVariablesAsync(
                new DebugVariablesRequest(locals.VariablesReference, 0, 1, false),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual("answer", answer.Name);
            Assert.AreEqual("42", answer.Value);
            Assert.AreEqual("int", answer.Type);
            _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsGreaterThan(0L, released.Length);
        }
        catch
        {
            fixture.PreserveFailure(TestContext);
            throw;
        }
    }
}
