using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies field-backed evaluation against the current runtime getter version after compiler updates.
/// </summary>
public sealed partial class DebuggerRpcHotReloadTests
{
    /// <summary>
    /// Reads the replacement field and local signature, then rejects a getter changed to mutate the target.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FastGetterInspectionTracksHotReloadBodies()
    {
        string directory = Directory.CreateTempSubdirectory("csls-getter-hotreload-").FullName;
        try
        {
            (string program, string source, int line, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitGetterGenerationsAsync(directory, TestContext.CancellationToken).ConfigureAwait(false);
            DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession.StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
            DebuggerRpcClient client = worker.Client;
            _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(
                source, [new DebugSourceBreakpointRequest(line, null)]), TestContext.CancellationToken).ConfigureAwait(false);
            _ = await client.LaunchAsync(new DebugLaunchRequest
            {
                Program = program,
                WorkingDirectory = directory,
                EnableHotReload = true
            }, TestContext.CancellationToken).ConfigureAwait(false);
            DebugSessionSnapshot stopped = await WaitForStateAsync(client, DebugSessionState.Stopped, TestContext.CancellationToken)
                .ConfigureAwait(false);
            DebugModuleInfo module = (await client.GetModulesAsync(new DebugModulesRequest(0, 0), TestContext.CancellationToken)
                .ConfigureAwait(false)).Modules.Single(item => DebuggerTestPath.AreEquivalent(item.Path, program));
            int threadId = stopped.StoppedThreadId ?? throw new AssertFailedException("The stopped caller has no thread.");
            DebugStackFrameInfo frame = (await client.GetStackAsync(new DebugStackRequest(threadId, 0, 16),
                TestContext.CancellationToken).ConfigureAwait(false)).StackFrames[0];
            DebugEvaluateResult initial = await client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "receiver.Value"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("10", initial.Result);
            Assert.IsFalse(initial.TargetCodeExecuted);
            for (int index = 0; index < updates.Count; index++)
            {
                HotReloadDeclarationUpdate update = updates[index];
                await File.WriteAllTextAsync(source, update.Source, Encoding.UTF8, TestContext.CancellationToken).ConfigureAwait(false);
                DebugHotReloadResult applied = await client.ApplyHotReloadAsync(new DebugHotReloadRequest(
                    stopped.StopGeneration, module.Id, index, update.Metadata, update.Il, update.Pdb,
                    update.Types, ["Baseline"], update.Methods, []), TestContext.CancellationToken).ConfigureAwait(false);
                frame = (await client.GetStackAsync(new DebugStackRequest(threadId, 0, 16),
                    TestContext.CancellationToken).ConfigureAwait(false)).StackFrames[0];
                if (index == 0)
                {
                    DebugEvaluateResult updated = await client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "receiver.Value"),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.AreEqual("42", updated.Result);
                    Assert.IsFalse(updated.TargetCodeExecuted);
                }
                else
                {
                    RemoteInvocationException denied = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(() =>
                        client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "receiver.Value"), TestContext.CancellationToken))
                        .ConfigureAwait(false);
                    Assert.Contains("requires target-code evaluation", denied.Message, StringComparison.Ordinal);
                }
                DebugEvaluateResult field = await client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "receiver._second"),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("42", field.Result);
                stopped = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(applied.StopGeneration, stopped.StopGeneration);
                Assert.AreEqual(DebugSessionState.Stopped, stopped.State);
            }

            _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, []), TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.ContinueAsync(TestContext.CancellationToken).ConfigureAwait(false);
            _ = await WaitForStateAsync(client, DebugSessionState.Terminated, TestContext.CancellationToken).ConfigureAwait(false);
            DebugOutputPage output = await client.GetOutputAsync(new DebugOutputRequest(0, 256), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("43", string.Concat(output.Entries.Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
