using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects captured physical object fields through real dumps and the supervised worker transport.
/// </summary>
[TestClass]
public sealed class DumpObjectTests : DapTestContext
{
    /// <summary>
    /// Preserves exact field storage, generic base types, boxed structs and mixed object-array paths.
    /// </summary>
    /// <param name="captureType">The actual runtime dump-writer policy.</param>
    [TestMethod]
    [DataRow(DumpType.WithHeap)]
    [DataRow(DumpType.Full)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedObjectsPreservePhysicalFields(DumpType captureType)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureArrayShapes: true, captureType: captureType,
            diagnosticContext: TestContext).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        try
        {
            DumpMachORegisterAssertions.Verify(fixture.DumpPath, TestContext.CancellationToken);
            await AssertDirectFieldsAsync(fixture).ConfigureAwait(false);
            AssertReleased(fixture);
            await AssertWorkerFieldsAsync(fixture).ConfigureAwait(false);
            AssertReleased(fixture);
        }
        catch
        {
            fixture.PreserveFailure(TestContext);
            throw;
        }
    }

    private async Task AssertDirectFieldsAsync(DebuggerDumpFixture fixture)
    {
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable cleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        await AssertFieldsAsync(service, () => service.GetSessionAsync(TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task AssertWorkerFieldsAsync(DebuggerDumpFixture fixture)
    {
        string workerPath = Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Dump.Worker", "debug",
            "csls-debugger-dump-worker.dll");
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(workerPath, false,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
        _ = await worker.Client.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        await AssertFieldsAsync(worker.Client, () => worker.Client.GetSessionAsync(TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await worker.Client.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static void AssertReleased(DebuggerDumpFixture fixture)
    {
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Preserves published field paths after canceled native reads and rejected result publication.
    /// </summary>
    /// <param name="failCompletion">Whether to reject completed publication instead of canceling a native read.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedObjectCancellationPreservesChildren(bool failCompletion)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, includeHeap: true, captureArrayShapes: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo chain = locals["chain"];
        DebugVariableInfo published = Assert.ContainsSingle(await ReadAsync(service, chain).ConfigureAwait(false));
        Assert.IsGreaterThan(0, published.VariablesReference, published.ToString());
        DebugSessionSnapshot before = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var failure = new IOException("Object observer closed.");
        var observer = new DumpReadProgressRecorder(cancellation, checkpoint: failCompletion ? 0 : 1,
            failure: failCompletion ? failure : null, failureState: DebugDumpReadState.Completed);
        var request = new DebugVariablesRequest(published.VariablesReference, 0, 1, false) { DumpReadProgress = observer };
        if (failCompletion)
        {
            InvalidOperationException rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.GetVariablesAsync(request, cancellation.Token)).ConfigureAwait(false);
            Assert.AreSame(failure, rejected.InnerException);
            Assert.AreEqual(DebugDumpReadState.Completed, observer.Updates[^1].State);
        }
        else
        {
            OperationCanceledException rejected = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                service.GetVariablesAsync(request, cancellation.Token)).ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, rejected.CancellationToken);
            Assert.AreEqual(DebugDumpReadState.Canceled, observer.Updates[^1].State);
            Assert.AreEqual(1L, observer.Updates[^1].MemoryReads + observer.Updates[^1].ContextReads);
        }
        Assert.AreEqual(published, Assert.ContainsSingle(await ReadAsync(service, chain).ConfigureAwait(false)));
        DebugVariableInfo box = Assert.ContainsSingle(await ReadAsync(service, published).ConfigureAwait(false));
        DebugVariableInfo vector = Assert.ContainsSingle(await ReadAsync(service, box).ConfigureAwait(false));
        Assert.AreSequenceEqual(["41", "42", "43"],
            (await ReadAsync(service, vector).ConfigureAwait(false)).Select(value => value.Value));
        Assert.AreEqual(before, await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Stops cyclic object traversal at the exact depth budget and preserves earlier field paths.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedObjectDepthLimitPreservesPublishedPaths()
    {
        long started = Stopwatch.GetTimestamp();
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, includeHeap: true, captureArrayShapes: true,
            diagnosticContext: TestContext).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        Log("Opening captured dump.");
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        Log("Reading captured locals.");
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo current = locals["cycleObject"];
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        long initialReads = 0;
        long finalReads = 0;
        for (int depth = 0; depth < 256; depth++)
        {
            if (depth % 32 == 0)
            {
                Log($"Reading nested selection {depth + 1}.");
            }
            DumpReadProgressRecorder? progress = depth is 0 or 255 ? new(observation) : null;
            DebugVariableInfo next = Assert.ContainsSingle(await ReadAsync(service, current, progress: progress)
                .ConfigureAwait(false));
            if (progress is not null)
            {
                DebugDumpReadProgress completed = Assert.ContainsSingle(progress.Updates.Where(
                    static update => update.State == DebugDumpReadState.Completed));
                Assert.AreEqual(DebugDumpReadState.Completed, completed.State);
                if (depth == 0)
                {
                    initialReads = completed.MemoryReads;
                }
                else
                {
                    finalReads = completed.MemoryReads;
                }
            }
            Assert.AreEqual("Value", next.Name);
            Assert.AreEqual(1, next.NamedVariables, next.ToString());
            Assert.IsGreaterThan(0, next.VariablesReference);
            Assert.AreNotEqual(current.VariablesReference, next.VariablesReference);
            current = next;
        }
        Log("Checking depth rejection and retained paths.");
        InvalidDataException rejected = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ReadAsync(service, current)).ConfigureAwait(false);
        Assert.Contains("256 nested selections", rejected.Message);
        Assert.AreEqual(1, Assert.ContainsSingle(await ReadAsync(service, locals["cycleObject"]).ConfigureAwait(false)).NamedVariables);
        Assert.AreEqual("29", Assert.ContainsSingle(await ReadAsync(service, locals["singletonObject"]).ConfigureAwait(false)).Value);
        Log("Completed depth and retained-path assertions.");
        Log($"Memory reads for the same object: initial={initialReads}, final={finalReads}.");
        Assert.IsGreaterThan(0L, initialReads);
        Assert.IsGreaterThan(0L, finalReads);
        Assert.IsLessThanOrEqualTo(initialReads, finalReads,
            "Expanding the same captured object must not reread its ancestors as the logical path grows.");

        void Log(string message) => TestContext.WriteLine(
            $"Dump object depth {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms: {message}");
    }

    private async Task AssertFieldsAsync(IDebuggerInspectionTarget service, Func<Task<DebugSessionSnapshot>> getSession)
    {
        DebugSessionSnapshot before = await getSession().ConfigureAwait(false);
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo captured = locals["capturedObject"];
        Assert.IsGreaterThan(0, captured.VariablesReference, captured.ToString());
        Assert.AreEqual(4, captured.NamedVariables);
        Assert.AreEqual(0, captured.IndexedVariables);
        IReadOnlyList<DebugVariableInfo> fields = await ReadAsync(service, captured).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Number", "Text", "Pair", "EvaluationSignalPath"], fields.Select(field => field.Name));
        Assert.AreEqual("42", fields[0].Value);
        Assert.AreEqual("int", fields[0].Type);
        Assert.AreEqual("\"answer!\"", fields[1].Value);
        Assert.AreEqual("string", fields[1].Type);
        Assert.AreEqual(0, fields[1].VariablesReference);
        Assert.AreEqual("(int, string)", fields[2].Type, fields[2].ToString());
        Assert.AreSequenceEqual(["42", "\"answer!\""],
            (await ReadAsync(service, fields[2]).ConfigureAwait(false)).Select(field => field.Value));
        Assert.IsTrue(fields.All(field => !field.IsIndexed && field.EvaluateName is null && field.MemoryReference is null));
        Assert.AreEqual(fields[1], Assert.ContainsSingle(await ReadAsync(service, captured, 1, 1).ConfigureAwait(false)));
        Assert.AreEqual(fields[^1], Assert.ContainsSingle(await ReadAsync(service, captured, 3, 4096).ConfigureAwait(false)));
        Assert.IsEmpty(await ReadAsync(service, captured, 4, 1).ConfigureAwait(false));
        Assert.IsEmpty(await ReadAsync(service, captured, int.MaxValue, 0).ConfigureAwait(false));
        Assert.IsEmpty(await ReadAsync(service, captured, 0, 1, DebugVariableFilter.Indexed).ConfigureAwait(false));
        Assert.AreSequenceEqual(fields, await ReadAsync(service, captured, filter: DebugVariableFilter.Named).ConfigureAwait(false));
        Assert.AreEqual(0, locals["emptyObject"].VariablesReference);
        Assert.AreEqual(0, locals["emptyObject"].NamedVariables);
        Assert.AreEqual("null", locals["absentObject"].Value);
        Assert.AreEqual("System.Runtime.CompilerServices.StrongBox<int>", locals["absentObject"].Type);
        Assert.AreEqual(0, locals["absentObject"].VariablesReference);
        Assert.AreEqual(1, locals["singletonObject"].NamedVariables);
        DebugVariableInfo singleton = Assert.ContainsSingle(await ReadAsync(service, locals["singletonObject"]).ConfigureAwait(false));
        Assert.AreEqual("Value", singleton.Name);
        Assert.AreEqual("29", singleton.Value);
        Assert.AreEqual("int", singleton.Type);
        IReadOnlyList<DebugVariableInfo> inherited = await ReadAsync(service, locals["inheritedObject"]).ConfigureAwait(false);
        Assert.AreEqual(3, locals["inheritedObject"].NamedVariables);
        Assert.AreSequenceEqual(["_items", "_size", "_version"], inherited.Select(field => field.Name));
        DebugVariableInfo size = Assert.ContainsSingle(inherited.Where(field => field.Name == "_size"));
        Assert.AreEqual("1", size.Value);
        DebugVariableInfo items = Assert.ContainsSingle(inherited.Where(field => field.Name == "_items"));
        Assert.AreEqual("int[]", items.Type, items.ToString());
        Assert.AreEqual("81", Assert.ContainsSingle(await ReadAsync(service, items, 0, 1).ConfigureAwait(false)).Value);
        IReadOnlyList<DebugVariableInfo> hidden = await ReadAsync(service, locals["hiddenFields"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["_value", "_value"], hidden.Select(field => field.Name));
        Assert.AreSequenceEqual(["int[]", "int[]"], hidden.Select(field => field.Type));
        Assert.AreNotEqual(hidden[0].VariablesReference, hidden[1].VariablesReference);
        Assert.AreEqual("201", Assert.ContainsSingle(await ReadAsync(service, hidden[0]).ConfigureAwait(false)).Value);
        Assert.AreEqual("101", Assert.ContainsSingle(await ReadAsync(service, hidden[1]).ConfigureAwait(false)).Value);
        Assert.AreSequenceEqual(hidden, await ReadAsync(service, locals["hiddenFields"]).ConfigureAwait(false));
        IReadOnlyList<DebugVariableInfo> boxed = await ReadAsync(service, locals["boxedPair"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Item1", "Item2"], boxed.Select(field => field.Name));
        Assert.AreSequenceEqual(["73", "\"captured pair\""], boxed.Select(field => field.Value));
        DebugVariableInfo array = Assert.ContainsSingle(await ReadAsync(service, locals["chain"]).ConfigureAwait(false));
        DebugVariableInfo box = Assert.ContainsSingle(await ReadAsync(service, array).ConfigureAwait(false));
        DebugVariableInfo vector = Assert.ContainsSingle(await ReadAsync(service, box).ConfigureAwait(false));
        Assert.AreSequenceEqual(["41", "42", "43"],
            (await ReadAsync(service, vector).ConfigureAwait(false)).Select(field => field.Value));
        Assert.AreEqual(vector, Assert.ContainsSingle(await ReadAsync(service, box).ConfigureAwait(false)));
        IReadOnlyList<DebugVariableInfo> inline = await ReadAsync(service, locals["inlineFields"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Pair", "Empty", "Escaped"], inline.Select(field => field.Name));
        Assert.AreEqual("System.Collections.Generic.KeyValuePair<int, (long, string)>", inline[0].Type, inline[0].ToString());
        Assert.AreEqual("\"\"", inline[1].Value);
        Assert.AreEqual("\"before\\0\\n\\uD800after\"", inline[2].Value);
        IReadOnlyList<DebugVariableInfo> pairFields = await ReadAsync(service, inline[0]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["key", "value"], pairFields.Select(field => field.Name));
        Assert.AreEqual("123", pairFields[0].Value);
        Assert.AreEqual("int", pairFields[0].Type);
        Assert.AreEqual("(long, string)", pairFields[1].Type, pairFields[1].ToString());
        IReadOnlyList<DebugVariableInfo> nestedFields = await ReadAsync(service, pairFields[1]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Item1", "Item2"], nestedFields.Select(field => field.Name));
        Assert.AreSequenceEqual(["long", "string"], nestedFields.Select(field => field.Type));
        Assert.AreSequenceEqual(["456", "\"inline-only\""], nestedFields.Select(field => field.Value));
        DebugVariableInfo cycle = Assert.ContainsSingle(await ReadAsync(service, locals["cycleObject"]).ConfigureAwait(false));
        Assert.AreEqual(1, cycle.NamedVariables);
        Assert.IsGreaterThan(0, cycle.VariablesReference);
        Assert.AreNotEqual(locals["cycleObject"].VariablesReference, cycle.VariablesReference);
        Assert.AreEqual(1, Assert.ContainsSingle(await ReadAsync(service, cycle).ConfigureAwait(false)).NamedVariables);
        Assert.AreSequenceEqual(fields, await ReadAsync(service, captured).ConfigureAwait(false));
        DebugSessionSnapshot after = await getSession().ConfigureAwait(false);
        Assert.AreEqual(before.State, after.State);
        Assert.AreEqual(before.StopGeneration, after.StopGeneration);
        Assert.AreEqual(before.ProcessId, after.ProcessId);
    }

    private Task<IReadOnlyList<DebugVariableInfo>> ReadAsync(IDebuggerInspectionTarget service, DebugVariableInfo value,
        int start = 0, int count = 0, DebugVariableFilter filter = DebugVariableFilter.All,
        IProgress<DebugDumpReadProgress>? progress = null) => service.GetVariablesAsync(
            new DebugVariablesRequest(value.VariablesReference, start, count, false, filter) { DumpReadProgress = progress },
            TestContext.CancellationToken);

    private async Task<Dictionary<string, DebugVariableInfo>> ReadLocalsAsync(IDebuggerInspectionTarget service)
    {
        IReadOnlyList<DebugThreadInfo> threads = await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        List<DebugStackFrameInfo> frames = [];
        foreach (DebugThreadInfo thread in threads)
        {
            DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(thread.Id, 0, 0),
                TestContext.CancellationToken).ConfigureAwait(false);
            frames.AddRange(stack.StackFrames);
        }
        DebugStackFrameInfo frame = Assert.ContainsSingle(frames.Where(frame =>
            frame.Name.Contains("DebuggerDumpArrayFixture.Run", StringComparison.Ordinal)), string.Join(Environment.NewLine, frames));
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(frame.Id),
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugScopeInfo locals = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
        IReadOnlyList<DebugVariableInfo> values = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 0, false), TestContext.CancellationToken).ConfigureAwait(false);
        return values.ToDictionary(value => value.Name);
    }
}
