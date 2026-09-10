using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies atomic source breakpoint replacement through real debugger-control streams.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Keeps an activated breakpoint executable after a later request in its replacement fails validation.
    /// </summary>
    /// <param name="invalidLine">The malformed one-based source line.</param>
    /// <param name="invalidColumn">The malformed optional one-based source column.</param>
    [TestMethod]
    [DataRow(0, null)]
    [DataRow(-1, null)]
    [DataRow(1, 0)]
    [DataRow(1, -1)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PrivateRpcRejectsSourceReplacementWithoutRemovingActiveBindings(int invalidLine, int? invalidColumn)
    {
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-validation-").FullName;
        try
        {
            string repository = FindRepositoryRoot();
            string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
            string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
            int line = Array.FindIndex(lines, static text => text.Contains("int localNumber = number + 1;", StringComparison.Ordinal)) + 1;
            Assert.IsGreaterThan(0, line);
            string signal = Path.Join(directory, "continue.signal");
            await File.WriteAllTextAsync(signal, "continue", TestContext.CancellationToken).ConfigureAwait(false);
            DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession.StartAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
            DebuggerRpcClient client = worker.Client;
            try
            {
                _ = await client.LaunchAsync(new DebugLaunchRequest
                {
                    Program = ResolveTestProcessHost(repository),
                    WorkingDirectory = repository,
                    Arguments = ["--debugger-fixture", signal],
                    SourceFileMap = CreateDefaultSourceFileMap(),
                    StopAtEntry = true
                }, TestContext.CancellationToken).ConfigureAwait(false);
                DebugSessionSnapshot entry = await WaitForStoppedAsync(client, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("entry", entry.StopReason);
                IReadOnlyList<DebugSourceBreakpointInfo> bound = await client.SetSourceBreakpointsAsync(
                    new DebugSourceBreakpointSetRequest(source, [new DebugSourceBreakpointRequest(line, null)]),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(Assert.ContainsSingle(bound).Verified);
                _ = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(() => client.SetSourceBreakpointsAsync(
                    new DebugSourceBreakpointSetRequest(source,
                    [
                        new DebugSourceBreakpointRequest(line, null, Condition: "false"),
                        new DebugSourceBreakpointRequest(invalidLine, invalidColumn)
                    ]), TestContext.CancellationToken)).ConfigureAwait(false);
                DebugSessionSnapshot unchanged = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(entry.StopGeneration, unchanged.StopGeneration);
                Assert.AreEqual(DebugSessionState.Stopped, unchanged.State);
                _ = await client.ContinueAsync(TestContext.CancellationToken).ConfigureAwait(false);
                DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("breakpoint", stopped.StopReason);
                Assert.IsGreaterThan(entry.StopGeneration, stopped.StopGeneration);
                int thread = stopped.StoppedThreadId ?? throw new AssertFailedException("The breakpoint did not report a stopped thread.");
                DebugStackTrace stack = await client.GetStackAsync(new DebugStackRequest(thread, 0, 1),
                    TestContext.CancellationToken).ConfigureAwait(false);
                DebugStackFrameInfo frame = Assert.ContainsSingle(stack.StackFrames);
                Assert.IsTrue(DebuggerTestPath.AreEquivalent(source, frame.Source?.Path));
                Assert.AreEqual(line, frame.Line);
                DebugEvaluateResult number = await client.EvaluateAsync(new DebugEvaluateRequest(frame.Id, "number"),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("42", number.Result);
                Assert.AreEqual("int", number.Type);
                DebugSessionSnapshot terminated = await client.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
            }
            catch
            {
                await worker.CaptureFailureAsync(TestContext).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }
}
