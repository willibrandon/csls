using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies compiler-driven Hot Reload through the real private RPC and CoreCLR boundary.
/// </summary>
[TestClass]
public sealed class DebuggerRpcHotReloadTests
{
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();

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
            ReportProgress("Deleting the target directory.");
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            ReportProgress("Target directory deleted.");
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
            ReportProgress("Deleting the target directory.");
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            ReportProgress("Target directory deleted.");
        }
    }

    private async Task ExerciseActiveMethodAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        ReportProgress("Emitting the active-method target and compiler deltas.");
        (
            string programPath,
            string sourcePath,
            string updatedSource,
            int breakpointLine,
            DebugHotReloadActiveStatement activeStatement,
            byte[] metadataDelta,
            byte[] ilDelta,
            byte[] pdbDelta,
            int[] updatedTypes,
            int[] updatedMethods) = await HotReloadTestCompilation.EmitActiveMethodAsync(
                testDirectory,
                cancellationToken).ConfigureAwait(false);
        ReportProgress("Starting the debugger worker and connecting private RPC.");
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        ReportProgress("Setting the active-method source breakpoint.");
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Launching the active-method target with Hot Reload enabled.");
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
        ReportProgress("Reading the stopped target's module capabilities.");
        DebugModuleInfo module = (await client.GetModulesAsync(
            new DebugModulesRequest(0, 0),
            cancellationToken).ConfigureAwait(false)).Modules.Single(item =>
                DebuggerTestPath.AreEquivalent(item.Path, programPath));
        Assert.IsTrue(module.IsHotReloadEnabled, module.HotReloadDiagnostic);
        Assert.Contains("Baseline", module.HotReloadCapabilities);
        Assert.Contains("AddFieldRva", module.HotReloadCapabilities);
        ReportProgress("Clearing the active-method source breakpoint.");
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(sourcePath, []),
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Writing the updated active-method source.");
        await File.WriteAllTextAsync(
            sourcePath,
            updatedSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Applying compiler deltas with an active-statement remap.");
        DebugHotReloadResult applied = await client.ApplyHotReloadAsync(
            new DebugHotReloadRequest(
                stopped.StopGeneration,
                module.Id,
                module.HotReloadGeneration,
                metadataDelta,
                ilDelta,
                pdbDelta,
                updatedTypes,
                ["Baseline"],
                updatedMethods,
                [activeStatement]),
            cancellationToken).ConfigureAwait(false);

        ReportProgress("Continuing the remapped active method.");
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        _ = await WaitForStateAsync(
            client,
            DebugSessionState.Terminated,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Reading and verifying the remapped target's output.");
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
        ReportProgress("Active-method assertions passed; disposing the debugger worker.");
    }

    private async Task ExerciseAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        ReportProgress("Emitting the target and compiler deltas.");
        (
            string programPath,
            string sourcePath,
            string updatedSource,
            int breakpointLine,
            int updatedValueLine,
            byte[] metadataDelta,
            byte[] ilDelta,
            byte[] pdbDelta,
            int[] updatedTypes,
            int[] updatedMethods) = await HotReloadTestCompilation.EmitAsync(
                testDirectory,
                cancellationToken).ConfigureAwait(false);
        string continuePath = Path.Join(testDirectory, "continue.signal");
        ReportProgress("Starting the debugger worker and connecting private RPC.");
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        ReportProgress("Setting the source breakpoint.");
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Setting the replacement method's function breakpoint.");
        _ = await client.SetFunctionBreakpointsAsync(
            new DebugFunctionBreakpointSetRequest(
                [new DebugFunctionBreakpointRequest("Program.Value")]),
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Launching the target with Hot Reload enabled.");
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
        ReportProgress("Reading the stopped target's module capabilities.");
        DebugModulePage modules = await client.GetModulesAsync(
            new DebugModulesRequest(0, 0),
            cancellationToken).ConfigureAwait(false);
        DebugModuleInfo module = modules.Modules.Single(item =>
            DebuggerTestPath.AreEquivalent(item.Path, programPath));
        Assert.IsTrue(module.IsHotReloadEnabled, module.HotReloadDiagnostic);
        Assert.Contains("Baseline", module.HotReloadCapabilities);
        Assert.Contains("AddFieldRva", module.HotReloadCapabilities);
        Assert.AreEqual(0, module.HotReloadGeneration);

        ReportProgress("Writing the updated source.");
        await File.WriteAllTextAsync(
            sourcePath,
            updatedSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Applying the compiler deltas.");
        DebugHotReloadResult applied = await client.ApplyHotReloadAsync(
            new DebugHotReloadRequest(
                stopped.StopGeneration,
                module.Id,
                module.HotReloadGeneration,
                metadataDelta,
                ilDelta,
                pdbDelta,
                updatedTypes,
                ["Baseline"],
                updatedMethods,
                []),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(module.Id, applied.ModuleId);
        Assert.AreEqual(1, applied.ModuleGeneration);
        Assert.AreEqual(stopped.StopGeneration + 1, applied.StopGeneration);
        Assert.IsNotEmpty(applied.UpdatedMethods);
        Assert.IsNotEmpty(applied.UpdatedTypes);
        ReportProgress("Verifying session and module generations after applying deltas.");
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

        ReportProgress("Signaling the target to enter the replacement method.");
        await File.WriteAllTextAsync(
            continuePath,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Continuing to the replacement method's breakpoint.");
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot updatedMethodStop = await WaitForStateAsync(
            client,
            DebugSessionState.Stopped,
            cancellationToken).ConfigureAwait(false);
        Assert.IsGreaterThan(applied.StopGeneration, updatedMethodStop.StopGeneration);
        int threadId = updatedMethodStop.StoppedThreadId
            ?? throw new AssertFailedException("The updated method stop has no thread.");
        ReportProgress("Reading and verifying the replacement method's stack.");
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
        ReportProgress("Continuing the replacement method to target exit.");
        _ = await client.ContinueAsync(cancellationToken).ConfigureAwait(false);
        _ = await WaitForStateAsync(
            client,
            DebugSessionState.Terminated,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Reading and verifying the replacement target's output.");
        DebugOutputPage output = await client.GetOutputAsync(
            new DebugOutputRequest(0, 256),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "2",
            string.Concat(output.Entries
                .Where(static entry => entry.Category == DebugOutputCategory.StandardOutput)
                .Select(static entry => entry.Output)));
        ReportProgress("Replacement-method assertions passed; disposing the debugger worker.");
    }

    private async Task<DebugSessionSnapshot> WaitForStateAsync(
        DebuggerRpcClient client,
        DebugSessionState state,
        CancellationToken cancellationToken)
    {
        ReportProgress($"Waiting for target state {state}.");
        DebugSessionState? previousState = null;
        long previousGeneration = -1;
        while (true)
        {
            DebugSessionSnapshot snapshot = await client.GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            string description = $"state={snapshot.State}, process={snapshot.ProcessId}, " +
                $"generation={snapshot.StopGeneration}, reason={snapshot.StopReason}, " +
                $"thread={snapshot.StoppedThreadId}, exitCode={snapshot.ExitCode}";
            if (snapshot.State != previousState || snapshot.StopGeneration != previousGeneration)
            {
                ReportProgress($"Observed {description}; waiting for {state}.");
                previousState = snapshot.State;
                previousGeneration = snapshot.StopGeneration;
            }

            if (snapshot.State == state)
            {
                return snapshot;
            }

            if (snapshot.State is DebugSessionState.Faulted or DebugSessionState.Terminated)
            {
                Assert.Fail($"The Hot Reload target ended before reaching {state}: {description}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ReportProgress(string phase)
    {
        TestContext.WriteLine(FormattableString.Invariant(
            $"Hot Reload +{Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds:F3}s: {phase}"));
    }
}
