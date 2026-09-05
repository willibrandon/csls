using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies current declaration metadata and source stepping after compiler-driven Hot Reload.
/// </summary>
public sealed partial class DebuggerRpcHotReloadTests
{
    /// <summary>
    /// Resolves a newly added method's source and declared argument types before assigning and stepping it.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcInspectsAndAssignsAddedMethodArguments()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-debugger-added-method-hotreload-");
        try
        {
            await ExerciseAsync(directory.FullName, TestContext.CancellationToken, addMethod: true).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inspects and assigns reference locals introduced by a replacement method body before stepping it.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcInspectsAndAssignsUpdatedLocalDeclarations()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-debugger-declarations-hotreload-");
        try
        {
            await ExerciseAsync(directory.FullName, TestContext.CancellationToken, updateLocalDeclarations: true)
                .ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private async Task InspectUpdatedLocalsAsync(
        DebuggerRpcClient client, string sourcePath, string updatedSource, DebugSessionSnapshot methodStop,
        CancellationToken cancellationToken, string expectedType = "System.ArgumentException",
        bool rejectReverseAssignment = false, string expectedMethodName = "Program.Value",
        string targetName = "target", string sourceName = "source")
    {
        int line = updatedSource.Split('\n').Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(candidate => candidate.Text.Contains($"GC.KeepAlive({sourceName});", StringComparison.Ordinal)).Line;
        _ = await client.SetFunctionBreakpointsAsync(new DebugFunctionBreakpointSetRequest([]), cancellationToken)
            .ConfigureAwait(false);
        _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(
            sourcePath, [new DebugSourceBreakpointRequest(line, null)]), cancellationToken).ConfigureAwait(false);
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStateAsync(client, DebugSessionState.Stopped, cancellationToken)
            .ConfigureAwait(false);
        Assert.IsGreaterThan(methodStop.StopGeneration, stopped.StopGeneration);
        Assert.AreEqual("breakpoint", stopped.StopReason);
        int threadId = stopped.StoppedThreadId ?? throw new AssertFailedException("The breakpoint stop has no thread identifier.");
        DebugStackTrace stack = await client.GetStackAsync(new DebugStackRequest(threadId, 0, 16), cancellationToken)
            .ConfigureAwait(false);
        DebugStackFrameInfo frame = Assert.ContainsSingle(stack.StackFrames.Where(candidate =>
            DebuggerTestPath.AreEquivalent(candidate.Source?.Path, sourcePath) && candidate.Line == line),
            string.Join(Environment.NewLine, stack.StackFrames.Select(static candidate =>
                $"{candidate.Name} {candidate.Source?.Path}:{candidate.Line}")));
        Assert.AreEqual(expectedMethodName, frame.Name);
        if (expectedMethodName == "Program.Added")
        {
            DebugEvaluateResult tupleElement = await client.EvaluateAsync(
                new DebugEvaluateRequest(frame.Id, "pair.first"), cancellationToken).ConfigureAwait(false);
            Assert.AreEqual("11", tupleElement.Result);
            Assert.AreEqual("int", tupleElement.Type);
            DebugEvaluateResult otherElement = await client.EvaluateAsync(
                new DebugEvaluateRequest(frame.Id, "pair.second"), cancellationToken).ConfigureAwait(false);
            Assert.AreEqual("12", otherElement.Result);
            Assert.AreEqual("int", otherElement.Type);
        }
        DebugEvaluateResult original = await client.EvaluateAsync(
            new DebugEvaluateRequest(frame.Id, $"{targetName}._message"), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"original\"", original.Result, original.Result);
        Assert.AreEqual("string", original.Type);
        if (rejectReverseAssignment)
        {
            RemoteInvocationException rejected = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                () => client.SetExpressionAsync(new DebugSetExpressionRequest(
                    stopped.StopGeneration, frame.Id, sourceName, targetName), cancellationToken)).ConfigureAwait(false);
            Assert.Contains("No implicit reference conversion", rejected.Message, StringComparison.Ordinal);
            DebugEvaluateResult preservedSource = await client.EvaluateAsync(
                new DebugEvaluateRequest(frame.Id, $"{sourceName}._message"), cancellationToken).ConfigureAwait(false);
            Assert.AreEqual("\"replacement\"", preservedSource.Result);
            Assert.AreEqual("string", preservedSource.Type);
        }

        DebugAssignmentResult assigned = await client.SetExpressionAsync(new DebugSetExpressionRequest(
            stopped.StopGeneration, frame.Id, targetName, sourceName), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(stopped.StopGeneration, assigned.StopGeneration);
        Assert.IsFalse(assigned.TargetCodeExecuted);
        Assert.AreEqual(expectedType, assigned.Variable.Type);
        DebugEvaluateResult replacement = await client.EvaluateAsync(
            new DebugEvaluateRequest(frame.Id, $"{targetName}._message"), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"replacement\"", replacement.Result);
        Assert.AreEqual("string", replacement.Type);
        _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(sourcePath, []), cancellationToken)
            .ConfigureAwait(false);
        _ = await client.StepAsync(new DebugStepRequest(threadId, DebugStepKind.Over), cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stepped = await WaitForStateAsync(client, DebugSessionState.Stopped, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("step", stepped.StopReason);
        Assert.IsGreaterThan(stopped.StopGeneration, stepped.StopGeneration);
        DebugStackTrace newStack = await client.GetStackAsync(new DebugStackRequest(threadId, 0, 16), cancellationToken)
            .ConfigureAwait(false);
        DebugStackFrameInfo newFrame = Assert.ContainsSingle(newStack.StackFrames.Where(candidate =>
            DebuggerTestPath.AreEquivalent(candidate.Source?.Path, sourcePath) && candidate.Line == line + 1));
        Assert.AreNotEqual(frame.Id, newFrame.Id);
        DebugEvaluateResult afterStep = await client.EvaluateAsync(
            new DebugEvaluateRequest(newFrame.Id, $"{targetName}._message"), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"replacement\"", afterStep.Result);
        Assert.AreEqual("string", afterStep.Type);
    }

    /// <summary>
    /// Preserves old active-frame declarations while inspecting newly entered frames from successive updates.
    /// </summary>
    /// <param name="renameUpdatedLocals">Whether the second edit also moves and renames the local declarations.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcKeepsLocalDeclarationsAcrossSuccessiveUpdates(bool renameUpdatedLocals)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-debugger-declaration-generations-");
        try
        {
            (string program, string source, int line, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitDeclarationGenerationsAsync(directory.FullName, TestContext.CancellationToken,
                    renameUpdatedLocals)
                    .ConfigureAwait(false);
            DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession.StartAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
            DebuggerRpcClient client = worker.Client;
            _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(
                source, [new DebugSourceBreakpointRequest(line, null)]), TestContext.CancellationToken).ConfigureAwait(false);
            _ = await client.LaunchAsync(new DebugLaunchRequest
            {
                Program = program,
                WorkingDirectory = directory.FullName,
                EnableHotReload = true
            }, TestContext.CancellationToken).ConfigureAwait(false);
            DebugSessionSnapshot stopped = await WaitForStateAsync(client, DebugSessionState.Stopped, TestContext.CancellationToken)
                .ConfigureAwait(false);
            DebugModuleInfo module = (await client.GetModulesAsync(new DebugModulesRequest(0, 0), TestContext.CancellationToken)
                .ConfigureAwait(false)).Modules.Single(item => DebuggerTestPath.AreEquivalent(item.Path, program));
            _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, []), TestContext.CancellationToken)
                .ConfigureAwait(false);
            for (int index = 0; index < updates.Count; index++)
            {
                HotReloadDeclarationUpdate update = updates[index];
                await File.WriteAllTextAsync(source, update.Source, Encoding.UTF8, TestContext.CancellationToken).ConfigureAwait(false);
                DebugHotReloadResult applied = await client.ApplyHotReloadAsync(new DebugHotReloadRequest(
                    stopped.StopGeneration, module.Id, index, update.Metadata, update.Il, update.Pdb,
                    update.Types, ["Baseline"], update.Methods, []), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(index + 1, applied.ModuleGeneration);
                Assert.AreEqual(stopped.StopGeneration + 1, applied.StopGeneration);
                if (index > 0)
                {
                    int threadId = stopped.StoppedThreadId ?? throw new AssertFailedException("The active method has no stopped thread identifier.");
                    DebugStackTrace oldStack = await client.GetStackAsync(
                        new DebugStackRequest(threadId, 0, 16), TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    DebugStackFrameInfo oldFrame = Assert.ContainsSingle(oldStack.StackFrames.Where(
                        static frame => frame.Name == "Program.Value"));
                    int oldLine = updates[index - 1].Source.Split('\n').Select(static (text, line) => (Text: text, Line: line + 1))
                        .Single(static item => item.Text.Contains("GC.KeepAlive(target);", StringComparison.Ordinal)).Line;
                    Assert.AreEqual(oldLine, oldFrame.Line);
                    DebugEvaluateResult oldValue = await client.EvaluateAsync(new DebugEvaluateRequest(oldFrame.Id, "target._message"),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.AreEqual("\"replacement\"", oldValue.Result);
                    Assert.AreEqual("string", oldValue.Type);
                    DebugAssignmentResult oldAssignment = await client.SetExpressionAsync(new DebugSetExpressionRequest(
                        applied.StopGeneration, oldFrame.Id, "source", "target"), TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    Assert.AreEqual("System.ArgumentException", oldAssignment.Variable.Type);
                    Assert.AreEqual(applied.StopGeneration, oldAssignment.StopGeneration);
                    Assert.IsFalse(oldAssignment.TargetCodeExecuted);
                }

                _ = await client.SetFunctionBreakpointsAsync(new DebugFunctionBreakpointSetRequest(
                    [new DebugFunctionBreakpointRequest("Program.Value")]), TestContext.CancellationToken).ConfigureAwait(false);
                _ = await client.ContinueAsync(TestContext.CancellationToken).ConfigureAwait(false);
                DebugSessionSnapshot methodStop = await WaitForStateAsync(client, DebugSessionState.Stopped, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await InspectUpdatedLocalsAsync(client, source, update.Source, methodStop, TestContext.CancellationToken,
                    index == 0 ? "System.ArgumentException" : "System.ArgumentNullException",
                    rejectReverseAssignment: index > 0,
                    targetName: renameUpdatedLocals && index > 0 ? "currentTarget" : "target",
                    sourceName: renameUpdatedLocals && index > 0 ? "currentSource" : "source").ConfigureAwait(false);
                stopped = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
            }

            _ = await client.ContinueAsync(TestContext.CancellationToken).ConfigureAwait(false);
            _ = await WaitForStateAsync(client, DebugSessionState.Terminated, TestContext.CancellationToken).ConfigureAwait(false);
            DebugOutputPage output = await client.GetOutputAsync(new DebugOutputRequest(0, 256), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("12", string.Concat(output.Entries.Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
