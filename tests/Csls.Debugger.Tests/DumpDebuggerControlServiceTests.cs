using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies runtime resolution and inspection of independently captured process dumps.
/// </summary>
[TestClass]
public sealed class DumpDebuggerControlServiceTests : DapTestContext
{
    /// <summary>
    /// Reads real primitive arguments and locals after the independently captured process has exited.
    /// </summary>
    /// <param name="includeHeap">Whether captured references include their heap contents.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedFrameRecoversPhysicalArgumentsAndLocals(bool includeHeap)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, includeHeap).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugSessionSnapshot opened = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken)
            .ConfigureAwait(false);
        int initialThreadId = opened.StoppedThreadId ?? throw new AssertFailedException("The dump has no selected thread.");
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
        DebugStackFrameInfo waiting = Assert.ContainsSingle(frames.Where(frame =>
            frame.Name.Contains("DebuggerBlockingWait.Wait", StringComparison.Ordinal)),
            "Readiness must follow entry into the unreleased wait before capturing frame values.");
        DebugStackTrace initialStack = await service.GetStackAsync(new DebugStackRequest(initialThreadId, 0, 0),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains(selected, initialStack.StackFrames,
            $"Selected thread {initialThreadId}; threads: {string.Join(", ", threads)}; initial stack: {string.Join(", ", initialStack.StackFrames)}");
        Assert.Contains(waiting, initialStack.StackFrames);
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(selected.Id),
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugScopeInfo arguments = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Arguments"));
        DebugScopeInfo locals = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
        IReadOnlyList<DebugVariableInfo> argumentValues = await service.GetVariablesAsync(
            new DebugVariablesRequest(arguments.VariablesReference, 0, 0, false), TestContext.CancellationToken).ConfigureAwait(false);
        TestContext.WriteLine(string.Join(Environment.NewLine, argumentValues));
        Assert.HasCount(8, argumentValues);
        Assert.AreEqual("number", argumentValues[2].Name);
        Assert.AreEqual("text", argumentValues[3].Name);
        bool filtered = OperatingSystem.IsWindows() && !includeHeap;
        if (filtered)
        {
            AssertFilteredValue(argumentValues[2]);
        }
        else
        {
            Assert.AreEqual("42", argumentValues[2].Value);
            Assert.AreEqual("int", argumentValues[2].Type);
        }
        if (includeHeap)
        {
            Assert.AreEqual("\"answer\"", argumentValues[3].Value);
            Assert.AreEqual(DebugVariablePresentationKind.Normal, argumentValues[3].PresentationKind);
        }
        else if (filtered)
        {
            AssertFilteredValue(argumentValues[3]);
        }
        else
        {
            Assert.AreEqual(DebugVariablePresentationKind.Unavailable, argumentValues[3].PresentationKind);
            Assert.Contains("0x80131305", argumentValues[3].Value);
        }
        IReadOnlyList<DebugVariableInfo> localValues = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, localValues);
        Assert.AreEqual("localNumber", localValues[0].Name);
        Assert.AreEqual("localLong", localValues[1].Name);
        if (filtered)
        {
            AssertFilteredValue(localValues[0]);
            AssertFilteredValue(localValues[1]);
        }
        else
        {
            Assert.AreEqual("43", localValues[0].Value);
            Assert.AreEqual("44", localValues[1].Value);
            Assert.AreEqual("long", localValues[1].Type);
        }
        IReadOnlyList<DebugVariableInfo> page = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 1, 1, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(localValues[1], Assert.ContainsSingle(page));
        IReadOnlyList<DebugVariableInfo> pastEnd = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, int.MaxValue, 1, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(pastEnd);
        IReadOnlyList<DebugVariableInfo> indexed = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 0, false, DebugVariableFilter.Indexed),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(indexed);
        _ = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 1, true), TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, -1, 1, false), TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 4097, false), TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(int.MaxValue, 0, 1, false), TestContext.CancellationToken)).ConfigureAwait(false);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync().ConfigureAwait(false);
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetVariablesAsync(
                new DebugVariablesRequest(locals.VariablesReference, 0, 1, false), cancelled.Token)).ConfigureAwait(false);
        }

        IReadOnlyList<DebugVariableInfo> afterRejections = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreSequenceEqual(localValues, afterRejections);
        DebugSessionSnapshot inspected = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, inspected.State);
        Assert.AreEqual(1L, inspected.StopGeneration);
        DebugSessionSnapshot closed = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, closed.State);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    private static void AssertFilteredValue(DebugVariableInfo value)
    {
        Assert.AreEqual("Captured value unavailable: storage was filtered when the dump was created.", value.Value);
        Assert.AreEqual(DebugVariablePresentationKind.Unavailable, value.PresentationKind);
        Assert.AreEqual(0, value.VariablesReference);
        Assert.IsNull(value.MemoryReference);
        Assert.IsNull(value.EvaluateName);
    }

    /// <summary>
    /// Rejects wrong or malformed DAC images before loading code, then accepts the exact DAC.
    /// </summary>
    /// <param name="mismatch">The independent image-identity field or file shape to invalidate.</param>
    [TestMethod]
    [DataRow("runtime")]
    [DataRow("timestamp")]
    [DataRow("image-size")]
    [DataRow("architecture")]
    [DataRow("truncated")]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ExplicitDacRequiresExactIdentityAndRecovers(string mismatch)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        string correctDac = Path.Join(runtimeDirectory, "mscordaccore.dll");
        string wrongImage = Path.Join(runtimeDirectory, "coreclr.dll");
        if (mismatch != "runtime")
        {
            wrongImage = Path.ChangeExtension(fixture.DumpPath, ".dll");
            File.Copy(correctDac, wrongImage);
            using FileStream image = File.Open(wrongImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new PEReader(image, PEStreamOptions.LeaveOpen);
            PEHeaders headers = reader.PEHeaders;
            using var writer = new BinaryWriter(image);
            switch (mismatch)
            {
                case "timestamp":
                    image.Position = headers.CoffHeaderStartOffset + sizeof(ushort) * 2;
                    writer.Write(headers.CoffHeader.TimeDateStamp ^ 1);
                    break;
                case "image-size":
                    image.Position = headers.PEHeaderStartOffset + 56;
                    writer.Write(1);
                    break;
                case "architecture":
                    image.Position = headers.CoffHeaderStartOffset;
                    writer.Write((ushort)(headers.CoffHeader.Machine == Machine.Amd64 ? Machine.Arm64 : Machine.Amd64));
                    break;
                case "truncated":
                    image.SetLength(2);
                    break;
                default:
                    Assert.Fail($"Unknown DAC image mutation {mismatch}.");
                    break;
            }
        }

        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        InvalidDataException failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenDumpAsync(
            new DebugDumpOpenRequest(fixture.DumpPath, DacPath: wrongImage), TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.Contains("debugging-library identity", failure.Message);
        DebugSessionSnapshot afterFailure = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, afterFailure.State);
        using (FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsGreaterThan(0L, released.Length);
        }

        DebugSessionSnapshot opened = await service.OpenDumpAsync(
            new DebugDumpOpenRequest(fixture.DumpPath, DacPath: correctDac), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, opened.State);
        Assert.AreEqual(fixture.ProcessId, opened.ProcessId);
        DebugModulePage modules = await service.GetModulesAsync(new DebugModulesRequest(0, 0),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("csls-test-process-host.dll", modules.Modules.Select(module => module.Name));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an installed runtime for a triage dump after its target has exited.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TriageDumpResolvesInstalledRuntime()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using (var target = DataTarget.LoadDump(fixture.DumpPath,
            new DataTargetOptions { SymbolPaths = [] }))
        {
            ClrInfo info = Assert.ContainsSingle(target.ClrVersions);
            TestContext.WriteLine($"Captured runtime: {info.ModuleInfo.FileName}; version {info.Version}; " +
                $"timestamp {info.ModuleInfo.IndexTimeStamp:X8}; image size {info.ModuleInfo.IndexFileSize:X8}.");
            foreach (DebugLibraryInfo library in info.DebuggingLibraries.Where(item => item.Kind == DebugLibraryKind.Dac))
            {
                TestContext.WriteLine($"DAC: {library.FileName}; {library.ArchivedUnder}; " +
                    $"timestamp {library.IndexTimeStamp:X8}; image size {library.IndexFileSize:X8}.");
            }
        }

        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugSessionSnapshot session = await service.OpenDumpAsync(
            new DebugDumpOpenRequest(fixture.DumpPath, 0, null), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, session.State);
        Assert.AreEqual(fixture.ProcessId, session.ProcessId);
        Assert.AreEqual("dump", session.StopReason);
        int stoppedThread = Assert.IsInstanceOfType<int>(session.StoppedThreadId);
        IReadOnlyList<DebugThreadInfo> threads = await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains(stoppedThread, threads.Select(thread => thread.Id));
        DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(stoppedThread, 0, 1),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, stack.StackFrames);
        Assert.IsGreaterThan(0, stack.TotalFrames.GetValueOrDefault());
        DebugModulePage modules = await service.GetModulesAsync(new DebugModulesRequest(0, 0),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("csls-test-process-host.dll", modules.Modules.Select(module => module.Name));
        DebugSessionSnapshot closed = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, closed.State);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }
}
