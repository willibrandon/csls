using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies compiler-driven Hot Reload through the real private RPC and CoreCLR boundary.
/// </summary>
[TestClass]
public sealed class DebuggerRpcHotReloadTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Applies one Roslyn delta generation and executes the replacement method body.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcAppliesCompilerDeltasToRealTarget()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-hotreload-{Guid.NewGuid():N}");
        try
        {
            await ExerciseAsync(testDirectory, TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Remaps an active old-version frame and resumes through its replacement continuation.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcRemapsActiveMethodToReplacementBody()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-active-hotreload-{Guid.NewGuid():N}");
        try
        {
            await ExerciseActiveMethodAsync(testDirectory, TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseActiveMethodAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        (
            string programPath,
            string sourcePath,
            string updatedSource,
            int breakpointLine,
            DebugHotReloadActiveStatement activeStatement,
            byte[] metadataDelta,
            byte[] ilDelta,
            byte[] pdbDelta) = await HotReloadTestCompilation.EmitActiveMethodAsync(
                testDirectory,
                cancellationToken).ConfigureAwait(false);
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            cancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = programPath,
                WorkingDirectory = testDirectory,
                EnableHotReload = true
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStateAsync(
            client,
            DebugSessionState.Stopped,
            cancellationToken).ConfigureAwait(false);
        DebugModuleInfo module = (await client.GetModulesAsync(
            new DebugModulesRequest(0, 0),
            cancellationToken).ConfigureAwait(false)).Modules.Single(item =>
                DebuggerTestPath.AreEquivalent(item.Path, programPath));
        Assert.IsTrue(module.IsHotReloadEnabled, module.HotReloadDiagnostic);
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(sourcePath, []),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            sourcePath,
            updatedSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        DebugHotReloadResult applied = await client.ApplyHotReloadAsync(
            new DebugHotReloadRequest(
                stopped.StopGeneration,
                module.Id,
                module.HotReloadGeneration,
                metadataDelta,
                ilDelta,
                pdbDelta,
                [activeStatement]),
            cancellationToken).ConfigureAwait(false);

        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        _ = await WaitForStateAsync(
            client,
            DebugSessionState.Terminated,
            cancellationToken).ConfigureAwait(false);
        DebugOutputPage output = await client.GetOutputAsync(
            new DebugOutputRequest(0, 256),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "11",
            string.Concat(output.Entries
                .Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)),
            $"The active frame did not resume through Hot Reload generation " +
                $"{applied.ModuleGeneration}.");
    }

    private static async Task ExerciseAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        (
            string programPath,
            string sourcePath,
            string updatedSource,
            int breakpointLine,
            int updatedValueLine,
            byte[] metadataDelta,
            byte[] ilDelta,
            byte[] pdbDelta) = await HotReloadTestCompilation.EmitAsync(
                testDirectory,
                cancellationToken).ConfigureAwait(false);
        string continuePath = Path.Join(testDirectory, "continue.signal");
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            cancellationToken).ConfigureAwait(false);
        _ = await client.SetFunctionBreakpointsAsync(
            new DebugFunctionBreakpointSetRequest(
                [new DebugFunctionBreakpointRequest("Program.Value")]),
            cancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = programPath,
                WorkingDirectory = testDirectory,
                Arguments = [continuePath],
                EnableHotReload = true
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStateAsync(
            client,
            DebugSessionState.Stopped,
            cancellationToken).ConfigureAwait(false);
        DebugModulePage modules = await client.GetModulesAsync(
            new DebugModulesRequest(0, 0),
            cancellationToken).ConfigureAwait(false);
        DebugModuleInfo module = modules.Modules.Single(item =>
            DebuggerTestPath.AreEquivalent(item.Path, programPath));
        Assert.IsTrue(module.IsHotReloadEnabled, module.HotReloadDiagnostic);
        Assert.AreEqual(0, module.HotReloadGeneration);

        await File.WriteAllTextAsync(
            sourcePath,
            updatedSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        DebugHotReloadResult applied = await client.ApplyHotReloadAsync(
            new DebugHotReloadRequest(
                stopped.StopGeneration,
                module.Id,
                module.HotReloadGeneration,
                metadataDelta,
                ilDelta,
                pdbDelta,
                []),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(module.Id, applied.ModuleId);
        Assert.AreEqual(1, applied.ModuleGeneration);
        Assert.AreEqual(stopped.StopGeneration + 1, applied.StopGeneration);
        Assert.IsNotEmpty(applied.UpdatedMethods);
        DebugSessionSnapshot afterApply = await client.GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, afterApply.State);
        Assert.AreEqual(applied.StopGeneration, afterApply.StopGeneration);
        DebugModulePage updatedModules = await client.GetModulesAsync(
            new DebugModulesRequest(0, 0),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            1,
            updatedModules.Modules.Single(item => item.Id == module.Id).HotReloadGeneration);

        await File.WriteAllTextAsync(
            continuePath,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot updatedMethodStop = await WaitForStateAsync(
            client,
            DebugSessionState.Stopped,
            cancellationToken).ConfigureAwait(false);
        Assert.IsGreaterThan(applied.StopGeneration, updatedMethodStop.StopGeneration);
        int threadId = updatedMethodStop.StoppedThreadId
            ?? throw new AssertFailedException("The updated method stop has no thread.");
        DebugStackTrace stack = await client.GetStackAsync(
            new DebugStackRequest(threadId, 0, 16),
            cancellationToken).ConfigureAwait(false);
        int[] sourceLines = [.. stack.StackFrames
            .Where(frame => DebuggerTestPath.AreEquivalent(frame.Source?.Path, sourcePath))
            .Select(static frame => frame.Line)];
        Assert.Contains(
            updatedValueLine,
            sourceLines,
            string.Join(Environment.NewLine, stack.StackFrames.Select(static frame =>
                $"{frame.Name} {frame.Source?.Path}:{frame.Line}")));
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        _ = await WaitForStateAsync(
            client,
            DebugSessionState.Terminated,
            cancellationToken).ConfigureAwait(false);
        DebugOutputPage output = await client.GetOutputAsync(
            new DebugOutputRequest(0, 256),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "2",
            string.Concat(output.Entries
                .Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)));
    }

    private static async Task<DebugSessionSnapshot> WaitForStateAsync(
        DebuggerRpcClient client,
        DebugSessionState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DebugSessionSnapshot snapshot = await client.GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.State == state)
            {
                return snapshot;
            }

            if (snapshot.State == DebugSessionState.Faulted)
            {
                Assert.Fail("The Hot Reload target faulted.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
