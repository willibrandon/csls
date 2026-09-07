using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Csls.Debugger.Dump;
using StreamJsonRpc;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies captured array shapes, bounded pages and cancellation recovery using independently terminated targets.
/// </summary>
[TestClass]
public sealed class DumpArrayTests : DapTestContext
{
    private string? _dumpPath;
    /// <summary>
    /// Preserves runtime bounds, category filtering, nested array paths and page identity over real dump inspection.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedArrayShapesUseRuntimeBounds()
    {
        DebuggerDumpFixture fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot before = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await AssertShapesAsync(service).ConfigureAwait(false);
        Assert.AreEqual(before, await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Carries captured child handles and exact indexed pages through the supervised worker transport.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcPreservesCapturedArrayPages()
    {
        DebuggerDumpFixture fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string workerPath = Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Dump.Worker", "debug",
            "csls-debugger-dump-worker.dll");
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(workerPath, false,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerCleanup = worker.ConfigureAwait(false);
        _ = await worker.Client.OpenDumpAsync(fixture.OpenRequest,
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot before = await worker.Client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await AssertShapesAsync(worker.Client).ConfigureAwait(false);
        DebugSessionSnapshot after = await worker.Client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(before.State, after.State);
        Assert.AreEqual(before.ProcessId, after.ProcessId);
        Assert.AreEqual(before.StopGeneration, after.StopGeneration);
        Assert.AreEqual(before.StoppedThreadId, after.StoppedThreadId);
        Assert.AreEqual(before.StopReason, after.StopReason);
        _ = await worker.Client.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Preserves published child paths after canceled native reads or failed terminal progress delivery.
    /// </summary>
    /// <param name="failCompletion">Whether the client rejects completion instead of canceling an active native callback.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedArrayCancellationPreservesChildren(bool failCompletion)
    {
        DebuggerDumpFixture fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo jagged = locals["jagged"];
        IReadOnlyList<DebugVariableInfo> published = await ReadAsync(service, jagged, 0, 1).ConfigureAwait(false);
        DebugVariableInfo first = Assert.ContainsSingle(published);
        DebugSessionSnapshot before = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var failure = new IOException("Array observer closed.");
        var observer = new DumpReadProgressRecorder(cancellation, checkpoint: failCompletion ? 0 : 1,
            failure: failCompletion ? failure : null, failureState: DebugDumpReadState.Completed);
        var request = new DebugVariablesRequest(jagged.VariablesReference, 1, 1, false) { DumpReadProgress = observer };
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
        Assert.AreSequenceEqual(published, await ReadAsync(service, jagged, 0, 1).ConfigureAwait(false));
        Assert.AreSequenceEqual(["41", "42", "43"],
            (await ReadAsync(service, first).ConfigureAwait(false)).Select(value => value.Value));
        Assert.AreEqual("91", Assert.ContainsSingle(await ReadAsync(service,
            Assert.ContainsSingle(await ReadAsync(service, jagged, 1, 1).ConfigureAwait(false))).ConfigureAwait(false)).Value);
        Assert.AreEqual(before, await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Rejects overflowing requests transactionally and preserves published paths at the exact retained-handle budget.
    /// </summary>
    /// <param name="failCompletion">Whether a completed native read also rejects publication before testing capacity.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedArrayHandleLimitRollsBackUnpublishedPaths(bool failCompletion)
    {
        DebuggerDumpFixture fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo many = locals["many"];
        int remaining = 65536 - locals.Values.Count(value => value.VariablesReference != 0);
        Assert.AreEqual(65537, many.IndexedVariables);
        if (failCompletion)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            var failure = new IOException("Reject unpublished array children.");
            var observer = new DumpReadProgressRecorder(cancellation, failure: failure, failureState: DebugDumpReadState.Completed);
            InvalidOperationException rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetVariablesAsync(
                new DebugVariablesRequest(many.VariablesReference, 65536, 1, false) { DumpReadProgress = observer },
                TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.AreSame(failure, rejected.InnerException);
        }
        int start = 0;
        DebugVariableInfo? first = null;
        while (remaining > 4096)
        {
            IReadOnlyList<DebugVariableInfo> page = await ReadAsync(service, many, start, 4096).ConfigureAwait(false);
            Assert.HasCount(4096, page);
            Assert.IsTrue(page.All(value => value.VariablesReference > 0));
            first ??= page[0];
            start += page.Count;
            remaining -= page.Count;
        }
        Assert.IsNotNull(first);
        Assert.IsInRange(1, 4095, remaining);
        InvalidDataException overflow = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ReadAsync(service, many, 65536 - remaining, remaining + 1)).ConfigureAwait(false);
        Assert.Contains("65536 paths", overflow.Message);
        IReadOnlyList<DebugVariableInfo> boundary = await ReadAsync(service, many, start, remaining).ConfigureAwait(false);
        Assert.HasCount(remaining, boundary);
        Assert.IsTrue(boundary.All(value => value.VariablesReference > 0));
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ReadAsync(service, many, start + remaining, 1)).ConfigureAwait(false);
        Assert.AreSequenceEqual(boundary, await ReadAsync(service, many, start, remaining).ConfigureAwait(false));
        Assert.AreEqual(first, Assert.ContainsSingle(await ReadAsync(service, many, 0, 1).ConfigureAwait(false)));
        Assert.AreSequenceEqual(["41", "42", "43"],
            (await ReadAsync(service, first).ConfigureAwait(false)).Select(value => value.Value));
    }

    /// <summary>
    /// Stops cyclic array expansion at the depth budget while retaining earlier valid paths.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedArrayDepthLimitPreservesPublishedPaths()
    {
        DebuggerDumpFixture fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        _ = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken).ConfigureAwait(false);
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo current = locals["cycle"];
        for (int depth = 0; depth < 256; depth++)
        {
            DebugVariableInfo next = Assert.ContainsSingle(await ReadAsync(service, current).ConfigureAwait(false));
            Assert.AreEqual(1, next.IndexedVariables);
            Assert.AreEqual("[0]", next.Name);
            Assert.IsGreaterThan(0, next.VariablesReference);
            Assert.AreNotEqual(current.VariablesReference, next.VariablesReference);
            current = next;
        }
        InvalidDataException rejected = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ReadAsync(service, current)).ConfigureAwait(false);
        Assert.Contains("256 nested selections", rejected.Message);
        Assert.AreEqual(1, Assert.ContainsSingle(await ReadAsync(service, locals["cycle"]).ConfigureAwait(false)).IndexedVariables);
        Assert.AreSequenceEqual(["41", "42", "43"],
            (await ReadAsync(service, locals["vector"]).ConfigureAwait(false)).Select(value => value.Value));
    }

    private async Task AssertShapesAsync(IDebuggerInspectionTarget service)
    {
        Dictionary<string, DebugVariableInfo> locals = await ReadLocalsAsync(service).ConfigureAwait(false);
        DebugVariableInfo vector = locals["vector"];
        Assert.AreEqual("int[]", vector.Type);
        Assert.AreEqual("int[]", locals["empty"].Type);
        Assert.AreEqual("int[]", locals["absent"].Type);
        Assert.AreEqual("int[,]", locals["rectangular"].Type);
        Assert.AreEqual("int[,]", locals["nonZero"].Type);
        Assert.AreEqual("int[*]", locals["nonVector"].Type);
        Assert.AreEqual("int[,]", locals["emptyDimension"].Type);
        Assert.AreEqual("int[][]", locals["jagged"].Type);
        Assert.AreEqual("string[]", locals["texts"].Type);
        Assert.AreEqual("object[]", locals["cycle"].Type);
        Assert.AreEqual("System.Collections.Generic.List<int>[]", locals["constructed"].Type);
        Assert.AreEqual("(int, string)[]", locals["tuples"].Type);
        Assert.AreEqual("int?[]", locals["nullable"].Type);
        Assert.AreEqual("decimal[]", locals["decimals"].Type);
        Assert.IsGreaterThan(0, vector.VariablesReference);
        Assert.AreEqual(3, vector.IndexedVariables);
        Assert.AreEqual(0, vector.NamedVariables);
        IReadOnlyList<DebugVariableInfo> elements = await ReadAsync(service, vector).ConfigureAwait(false);
        Assert.AreSequenceEqual(["41", "42", "43"], elements.Select(value => value.Value));
        Assert.AreSequenceEqual(["[0]", "[1]", "[2]"], elements.Select(value => value.Name));
        foreach (DebugVariableInfo element in elements)
        {
            Assert.AreEqual("int", element.Type);
            Assert.IsTrue(element.IsIndexed);
            Assert.AreEqual(0, element.VariablesReference);
            Assert.IsNull(element.EvaluateName);
        }
        Assert.AreEqual(elements[1], Assert.ContainsSingle(await ReadAsync(service, vector, 1, 1).ConfigureAwait(false)));
        Assert.AreEqual(elements[2], Assert.ContainsSingle(await ReadAsync(service, vector, 2, 4).ConfigureAwait(false)));
        Assert.IsEmpty(await ReadAsync(service, vector, 3, 1).ConfigureAwait(false));
        Assert.IsEmpty(await ReadAsync(service, vector, int.MaxValue, 0).ConfigureAwait(false));
        Assert.IsEmpty(await ReadAsync(service, vector, 1, 1, DebugVariableFilter.Named).ConfigureAwait(false));
        Assert.AreSequenceEqual(elements, await ReadAsync(service, vector, filter: DebugVariableFilter.Indexed).ConfigureAwait(false));
        Assert.AreEqual(0, locals["empty"].IndexedVariables);
        Assert.AreEqual(0, locals["empty"].VariablesReference);
        Assert.AreEqual(0, locals["emptyDimension"].IndexedVariables);
        Assert.AreEqual(0, locals["emptyDimension"].VariablesReference);
        Assert.AreEqual("null", locals["absent"].Value);
        Assert.AreEqual(0, locals["absent"].VariablesReference);
        Assert.AreEqual("91", Assert.ContainsSingle(await ReadAsync(service, locals["singleton"]).ConfigureAwait(false)).Value);
        IReadOnlyList<DebugVariableInfo> rectangular = await ReadAsync(service, locals["rectangular"], 2, 3).ConfigureAwait(false);
        Assert.AreSequenceEqual(["[0,2]", "[1,0]", "[1,1]"], rectangular.Select(value => value.Name));
        Assert.AreSequenceEqual(["13", "21", "22"], rectangular.Select(value => value.Value));
        IReadOnlyList<DebugVariableInfo> nonZero = await ReadAsync(service, locals["nonZero"], 2, 3).ConfigureAwait(false);
        Assert.AreSequenceEqual(["[-2,7]", "[-1,5]", "[-1,6]"], nonZero.Select(value => value.Name));
        Assert.AreSequenceEqual(["17", "25", "26"], nonZero.Select(value => value.Value));
        IReadOnlyList<DebugVariableInfo> nonVector = await ReadAsync(service, locals["nonVector"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["[-3]", "[-2]"], nonVector.Select(value => value.Name));
        Assert.AreSequenceEqual(["71", "72"], nonVector.Select(value => value.Value));
        IReadOnlyList<DebugVariableInfo> jagged = await ReadAsync(service, locals["jagged"]).ConfigureAwait(false);
        Assert.HasCount(4, jagged);
        Assert.AreSequenceEqual(["int[]", "int[]", "int[]", "int[]"], jagged.Select(value => value.Type));
        Assert.AreSequenceEqual(jagged, await ReadAsync(service, locals["jagged"]).ConfigureAwait(false));
        Assert.AreSequenceEqual(elements, await ReadAsync(service, jagged[0]).ConfigureAwait(false));
        Assert.AreSequenceEqual(elements, await ReadAsync(service, jagged[3]).ConfigureAwait(false));
        Assert.AreEqual(0, jagged[2].VariablesReference);
        IReadOnlyList<DebugVariableInfo> texts = await ReadAsync(service, locals["texts"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["\"captured\\ntext\"", "null", "\"last\""], texts.Select(value => value.Value));
        IReadOnlyList<DebugVariableInfo> constructed = await ReadAsync(service, locals["constructed"]).ConfigureAwait(false);
        Assert.AreSequenceEqual(["System.Collections.Generic.List<int>", "System.Collections.Generic.List<int>"],
            constructed.Select(value => value.Type));
        Assert.AreEqual("null", constructed[1].Value);
        DebugVariableInfo tupleElement = Assert.ContainsSingle(await ReadAsync(service, locals["tuples"]).ConfigureAwait(false));
        Assert.AreEqual("(int, string)", tupleElement.Type, tupleElement.ToString());
        Assert.AreSequenceEqual(["int?", "int?"],
            (await ReadAsync(service, locals["nullable"]).ConfigureAwait(false)).Select(value => value.Type));
        Assert.AreEqual("decimal", Assert.ContainsSingle(await ReadAsync(service, locals["decimals"]).ConfigureAwait(false)).Type);
        Assert.AreEqual(65537, locals["large"].IndexedVariables);
        IReadOnlyList<DebugVariableInfo> last = await ReadAsync(service, locals["large"], 65535, 2).ConfigureAwait(false);
        Assert.AreSequenceEqual(["65635", "65636"], last.Select(value => value.Value));
        Assert.HasCount(4096, await ReadAsync(service, locals["large"], 0, 4096).ConfigureAwait(false));
        await AssertRejectedAsync<InvalidDataException>(service, () => ReadAsync(service, locals["large"]),
            "response limit of 4096").ConfigureAwait(false);
        await AssertRejectedAsync<ArgumentOutOfRangeException>(service, () => ReadAsync(service, vector, -1, 1),
            "start must not be negative").ConfigureAwait(false);
        await AssertRejectedAsync<ArgumentOutOfRangeException>(service, () => ReadAsync(service, vector, 0, 4097),
            "count must be between zero and 4096").ConfigureAwait(false);
        await AssertRejectedAsync<NotSupportedException>(service, () => service.GetVariablesAsync(
            new DebugVariablesRequest(vector.VariablesReference, 0, 1, true), TestContext.CancellationToken),
            "target-code execution").ConfigureAwait(false);
        Assert.AreSequenceEqual(last, await ReadAsync(service, locals["large"], 65535, 2).ConfigureAwait(false));
        DebugVariableInfo cycle = Assert.ContainsSingle(await ReadAsync(service, locals["cycle"]).ConfigureAwait(false));
        Assert.AreEqual(1, cycle.IndexedVariables);
        Assert.IsGreaterThan(0, cycle.VariablesReference);
        Assert.AreEqual(1, Assert.ContainsSingle(await ReadAsync(service, cycle).ConfigureAwait(false)).IndexedVariables);
        Dictionary<string, DebugVariableInfo> repeated = await ReadLocalsAsync(service).ConfigureAwait(false);
        Assert.AreEqual(vector, repeated["vector"]);
    }

    private async Task<DebuggerDumpFixture> CreateFixtureAsync()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, includeHeap: true, captureArrayShapes: true).ConfigureAwait(false);
        _dumpPath = fixture.DumpPath;
        return fixture;
    }

    private static async Task AssertRejectedAsync<T>(IDebuggerInspectionTarget service, Func<Task> operation, string message)
        where T : Exception
    {
        Exception failure = service is DebuggerRpcClient
            ? await Assert.ThrowsExactlyAsync<RemoteInvocationException>(operation).ConfigureAwait(false)
            : await Assert.ThrowsExactlyAsync<T>(operation).ConfigureAwait(false);
        Assert.Contains(message, failure.Message);
    }

    private Task<IReadOnlyList<DebugVariableInfo>> ReadAsync(IDebuggerInspectionTarget service, DebugVariableInfo value,
        int start = 0, int count = 0, DebugVariableFilter filter = DebugVariableFilter.All) => service.GetVariablesAsync(
            new DebugVariablesRequest(value.VariablesReference, start, count, false, filter), TestContext.CancellationToken);

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
        DebugStackFrameInfo[] matching = [.. frames.Where(frame =>
            frame.Name.Contains("DebuggerDumpArrayFixture.Run", StringComparison.Ordinal))];
        if (matching.Length != 1 && _dumpPath is not null)
        {
            string diagnostics = Directory.CreateDirectory(Path.Join(FindRepositoryRoot(), "artifacts", "diagnostics", "dump-arrays")).FullName;
            string captured = Path.Join(diagnostics, $"missing-frame-{Guid.NewGuid():N}.dmp");
            File.Copy(_dumpPath, captured);
            TestContext.WriteLine($"Captured stack failure: {captured}");
        }
        DebugStackFrameInfo selected = Assert.ContainsSingle(matching,
            $"Captured stacks:{Environment.NewLine}{string.Join(Environment.NewLine, frames)}");
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(selected.Id),
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugScopeInfo locals = Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
        IReadOnlyList<DebugVariableInfo> values = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 0, false), TestContext.CancellationToken).ConfigureAwait(false);
        return values.ToDictionary(value => value.Name);
    }
}
