using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies captured value integrity against the actual Windows dump writer's filtering policy.
/// </summary>
[TestClass]
public sealed class DumpFilteredValueTests : DapTestContext
{
    /// <summary>
    /// Preserves retained scalar values and reports filtered storage explicitly after the target exits.
    /// </summary>
    /// <param name="captureType">The real Windows capture policy.</param>
    [TestMethod]
    [DataRow(DumpType.Normal)]
    [DataRow(DumpType.Triage)]
    [DataRow(DumpType.WithHeap)]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedScalarHonorsWindowsMemoryFiltering(DumpType captureType)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, captureType: captureType).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugSessionSnapshot opened = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken)
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
            new DebugVariablesRequest(arguments.VariablesReference, 2, 1, false), TestContext.CancellationToken).ConfigureAwait(false);
        IReadOnlyList<DebugVariableInfo> localValues = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, argumentValues);
        Assert.HasCount(2, localValues);
        Assert.AreEqual("number", argumentValues[0].Name);
        Assert.AreEqual("localNumber", localValues[0].Name);
        Assert.AreEqual("localLong", localValues[1].Name);
        Assert.AreEqual("int", argumentValues[0].Type);
        Assert.AreEqual("int", localValues[0].Type);
        Assert.AreEqual("long", localValues[1].Type);
        if (captureType == DumpType.Triage)
        {
            foreach (DebugVariableInfo value in argumentValues.Concat(localValues))
            {
                Assert.AreEqual(DebugVariablePresentationKind.Unavailable, value.PresentationKind);
                Assert.AreEqual("Captured value unavailable: storage was filtered when the dump was created.", value.Value);
                Assert.AreEqual(0, value.VariablesReference);
                Assert.IsNull(value.MemoryReference);
                Assert.IsNull(value.EvaluateName);
            }
        }
        else
        {
            Assert.AreEqual("42", argumentValues[0].Value);
            Assert.AreEqual("43", localValues[0].Value);
            Assert.AreEqual("44", localValues[1].Value);
            Assert.AreEqual(DebugVariablePresentationKind.Normal, argumentValues[0].PresentationKind);
        }
        IReadOnlyList<DebugVariableInfo> repeated = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 1, 1, false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(localValues[1], Assert.ContainsSingle(repeated));
        Assert.AreEqual(opened, await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false));
        _ = await service.DetachAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }
}
