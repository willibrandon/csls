using Csls.Debugger.Tests;
using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Retains failed MCP breakpoint waits before the session releases its owned processes.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Captures the owned worker and target while preserving the stopped MCP session.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task BreakpointWaitCapturePreservesStoppedTarget()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        JsonElement started = await CallAsync(mcp.Client, "debug_session_start", new Dictionary<string, object?>
        {
            ["program"] = EditorToolResolver.ResolveTestProcessHost(repository),
            ["workingDirectory"] = repository,
            ["stopAtEntry"] = true
        }, TestContext.CancellationToken).ConfigureAwait(false);
        string session = started.GetProperty("debugSession").GetString()!;
        int targetId = started.GetProperty("processId").GetInt32();
        JsonElement stopped = await WaitForImplicitEvaluationStopAsync(mcp, session, targetId).ConfigureAwait(false);
        using Process worker = await FindOwnedDebuggerWorkerAsync(targetId, mcp.LauncherProcess.Id,
            EditorToolResolver.ResolveDebuggerWorker(repository), TestContext.CancellationToken).ConfigureAwait(false);

        string directory = await CaptureMcpBreakpointWaitAsync(mcp, targetId).ConfigureAwait(false);
        string[] dumps = Directory.GetFiles(directory, "*.dmp");
        Assert.AreEqual($"process-{worker.Id}.dmp", Path.GetFileName(Assert.ContainsSingle(dumps)));
        using (FileStream dump = File.OpenRead(dumps[0]))
        {
            byte[] signature = new byte[4];
            await dump.ReadExactlyAsync(signature, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("7F454C46", Convert.ToHexString(signature));
        }
        foreach (int processId in new[] { targetId, worker.Id })
        {
            string report = await File.ReadAllTextAsync(Path.Join(directory, $"process-{processId}.proc.txt"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains($"Pid:\t{processId}\n", report);
            Assert.Contains("task/", report);
        }
        JsonElement observed = await CallAsync(mcp.Client, "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", observed.GetProperty("state").GetString());
        Assert.AreEqual(targetId, observed.GetProperty("processId").GetInt32());
        Assert.AreEqual(stopped.GetProperty("stopGeneration").GetInt64(), observed.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual(stopped.GetProperty("stoppedThreadId").GetInt32(), observed.GetProperty("stoppedThreadId").GetInt32());
        JsonElement stack = await CallAsync(mcp.Client, "debug_stack_get", new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = observed.GetProperty("stopGeneration").GetInt64(),
            ["threadId"] = observed.GetProperty("stoppedThreadId").GetInt32(),
            ["levels"] = 1
        }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
    }

    private async Task<JsonElement> WaitForImplicitEvaluationStopAsync(
        McpProcessSession mcp, string session, int targetId)
    {
        JsonElement lastState = default;
        try
        {
            return await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken,
                state => lastState = state).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            TestContext.WriteLine($"Breakpoint wait for target {targetId} failed: {failure}");
            TestContext.WriteLine($"Last MCP session state: {lastState}");
            if (OperatingSystem.IsLinux())
            {
                _ = await CaptureMcpBreakpointWaitAsync(mcp, targetId).ConfigureAwait(false);
            }
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<string> CaptureMcpBreakpointWaitAsync(McpProcessSession mcp, int targetId)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string repository = EditorToolResolver.FindRepositoryRoot();
        string directory = Path.Join(repository, "artifacts", "test-results",
            $"mcp-breakpoint-wait-{targetId}-{Guid.NewGuid():N}");
        try
        {
            using var target = Process.GetProcessById(targetId);
            using Process worker = await FindOwnedDebuggerWorkerAsync(targetId, mcp.LauncherProcess.Id,
                EditorToolResolver.ResolveDebuggerWorker(repository), cancellation.Token).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            _ = await LinuxDebuggerProcessCapture.CaptureKernelStateAsync(target, directory, cancellation.Token)
                .ConfigureAwait(false);
            _ = await LinuxDebuggerProcessCapture.CaptureAsync(worker, directory, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or
            DiagnosticsClientException or AssertFailedException)
        {
            TestContext.WriteLine($"MCP breakpoint wait capture for {targetId}: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string artifact in Directory.EnumerateFiles(directory))
                {
                    TestContext.AddResultFile(artifact);
                }
            }
        }
        return directory;
    }
}
