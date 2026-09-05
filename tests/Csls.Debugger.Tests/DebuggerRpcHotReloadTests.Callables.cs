using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicit evaluation of methods introduced and replaced through real compiler updates.
/// </summary>
public sealed partial class DebuggerRpcHotReloadTests
{
    /// <summary>
    /// Evaluates added callable members through two generations while preserving existing stopped locals.
    /// </summary>
    /// <param name="kind">The static method, instance method, or constructor to evaluate.</param>
    [TestMethod]
    [DataRow("static")]
    [DataRow("instance")]
    [DataRow("constructor")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcEvaluatesMethodsAddedByHotReload(string kind)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-debugger-callables-hotreload-");
        try
        {
            (string program, string source, int line, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitCallableGenerationsAsync(directory.FullName, TestContext.CancellationToken)
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
            Assert.Contains("AddMethodToExistingType", module.HotReloadCapabilities);
            _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, []), TestContext.CancellationToken)
                .ConfigureAwait(false);
            for (int index = 0; index < updates.Count; index++)
            {
                HotReloadDeclarationUpdate update = updates[index];
                await File.WriteAllTextAsync(source, update.Source, Encoding.UTF8, TestContext.CancellationToken).ConfigureAwait(false);
                DebugHotReloadResult applied = await client.ApplyHotReloadAsync(new DebugHotReloadRequest(
                    stopped.StopGeneration, module.Id, index, update.Metadata, update.Il, update.Pdb,
                    update.Types, ["Baseline", "AddMethodToExistingType"], update.Methods, []), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(index + 1, applied.ModuleGeneration);
                Assert.HasCount(3, applied.UpdatedMethods);
                Assert.IsNotNull(stopped.StoppedThreadId);
                DebugStackFrameInfo frame = (await client.GetStackAsync(new DebugStackRequest(
                    stopped.StoppedThreadId.Value, 0, 16), TestContext.CancellationToken).ConfigureAwait(false)).StackFrames[0];
                Assert.AreEqual("Program.Main", frame.Name);
                Assert.AreEqual(line, frame.Line);
                string expression = kind switch
                {
                    "static" => "Program.Added(derivedSource)",
                    "instance" => "receiver.Added(5)",
                    "constructor" => "new Receiver(\"20\")",
                    _ => throw new AssertFailedException($"Unknown callable kind: {kind}")
                };
                DebugEvaluateResult result = await client.ExecuteExpressionAsync(new DebugExecuteExpressionRequest(
                    frame.Id, expression), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(result.TargetCodeExecuted);
                DebugSessionSnapshot evaluated = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsGreaterThan(applied.StopGeneration, evaluated.StopGeneration);
                Assert.AreEqual(DebugSessionState.Stopped, evaluated.State);
                DebugStackFrameInfo retained = (await client.GetStackAsync(new DebugStackRequest(
                    stopped.StoppedThreadId.Value, 0, 16), TestContext.CancellationToken).ConfigureAwait(false)).StackFrames[0];
                Assert.AreEqual(frame.Id, retained.Id);
                Assert.AreEqual(line, retained.Line);
                if (kind == "instance")
                {
                    Assert.AreEqual((16 + index).ToString(CultureInfo.InvariantCulture), result.Result);
                    Assert.AreEqual("int", result.Type);
                    Assert.AreEqual(0, result.VariablesReference);
                }
                else
                {
                    string expectedType = kind == "static" ? "System.ArgumentException" : "Receiver";
                    Assert.AreEqual(expectedType, result.Type);
                    IReadOnlyList<DebugVariableInfo> children = await client.GetVariablesAsync(new DebugVariablesRequest(
                        result.VariablesReference, 0, 64, AllowTargetCodeExecution: false), TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    DebugVariableInfo child = Assert.ContainsSingle(children.Where(variable =>
                        variable.Name == (kind == "static" ? "_message" : "_value")));
                    Assert.AreEqual(kind == "static" ? index == 0 ? "\"replacement\"" : "\"replacement-v2\""
                        : (21 + index).ToString(CultureInfo.InvariantCulture), child.Value);
                    Assert.AreEqual(kind == "static" ? "string" : "int", child.Type);
                }

                if (kind == "static")
                {
                    await AssertAddedCallAssignmentAsync(client, frame.Id, evaluated.StopGeneration, expression, index,
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

                DebugEvaluateResult original = await client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "receiver._value"),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("10", original.Result);
                Assert.AreEqual("int", original.Type);
                stopped = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
            }

            _ = await client.ContinueAsync(TestContext.CancellationToken).ConfigureAwait(false);
            _ = await WaitForStateAsync(client, DebugSessionState.Terminated, TestContext.CancellationToken).ConfigureAwait(false);
            DebugOutputPage output = await client.GetOutputAsync(new DebugOutputRequest(0, 256), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("10", string.Concat(output.Entries.Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task AssertAddedCallAssignmentAsync(DebuggerRpcClient client, int frameId, long generation,
        string expression, int index, CancellationToken cancellationToken)
    {
        DebugAssignmentResult assigned = await client.SetExpressionAsync(new DebugSetExpressionRequest(
            generation, frameId, "baseTarget", expression), cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(assigned.TargetCodeExecuted);
        Assert.IsGreaterThan(generation, assigned.StopGeneration);
        Assert.AreEqual("System.ArgumentException", assigned.Variable.Type);
        DebugEvaluateResult value = await client.EvaluateAsync(new DebugEvaluateRequest(frameId, "baseTarget._message"),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(index == 0 ? "\"replacement\"" : "\"replacement-v2\"", value.Result);
        RemoteInvocationException failure = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(() =>
            client.SetExpressionAsync(new DebugSetExpressionRequest(assigned.StopGeneration, frameId,
                "derivedSource", expression), cancellationToken)).ConfigureAwait(false);
        Assert.Contains("No implicit reference conversion", failure.Message, StringComparison.Ordinal);
        DebugSessionSnapshot rejected = await client.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsGreaterThan(assigned.StopGeneration, rejected.StopGeneration);
        Assert.AreEqual(DebugSessionState.Stopped, rejected.State);
        DebugEvaluateResult preserved = await client.EvaluateAsync(new DebugEvaluateRequest(frameId, "derivedSource._message"),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"replacement\"", preserved.Result);
    }
}
