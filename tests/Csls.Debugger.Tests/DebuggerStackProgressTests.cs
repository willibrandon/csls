using Csls.Debugger.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native stack cancellation and ownership through an isolated real-engine client.
/// </summary>
[TestClass]
public sealed class DebuggerStackProgressTests
{
    private static readonly int[] s_pageCheckpoints = [256, 512, 768, 1000];

    /// <summary>
    /// Gets the framework-managed cancellation token and evidence output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancels at an actual traversal checkpoint and preserves published frames after rollback.
    /// </summary>
    /// <param name="startFrame">The requested offset into the real recursive stack.</param>
    /// <param name="checkpoint">The observed traversal count that triggers cancellation.</param>
    /// <param name="capturedFrames">The expected selected frames before cancellation.</param>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [DataRow(90000, 256, 0)]
    [DataRow(256, 512, 256)]
    [DataRow(0, 256, 256)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NativeStackCancellationPreservesPublishedFrames(int startFrame, int checkpoint, int capturedFrames)
    {
        using JsonDocument document = await RunProbeAsync("cancel", startFrame, checkpoint).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.Contains("CanceledException", result.GetProperty("failureType").GetString()!);
        DebugStackWalkProgress[] updates = ReadUpdates(result);
        Assert.HasCount(checkpoint / 256 + 1, updates);
        DebugStackWalkProgress walking = updates[^2];
        Assert.AreEqual(DebugStackWalkState.Walking, walking.State);
        Assert.AreEqual(result.GetProperty("stopped").GetProperty("StoppedThreadId").GetInt32(), walking.ThreadId);
        Assert.AreEqual(checkpoint, walking.InspectedFrames);
        Assert.AreEqual(capturedFrames, walking.CapturedFrames);
        Assert.AreEqual(1 + capturedFrames - (startFrame == 0 ? 1 : 0), walking.RetainedFrameBindings);
        Assert.AreEqual(3, walking.OwnedWalkInterfaces);
        DebugStackWalkProgress terminal = updates[^1];
        Assert.AreEqual(DebugStackWalkState.Canceled, terminal.State);
        Assert.AreEqual(checkpoint, terminal.InspectedFrames);
        Assert.AreEqual(capturedFrames, terminal.CapturedFrames);
        Assert.AreEqual(1, terminal.RetainedFrameBindings);
        Assert.AreEqual(0, terminal.OwnedWalkInterfaces);
        Assert.AreEqual(1, result.GetProperty("recovery").GetProperty("RetainedFrameBindings").GetInt32());
        AssertRecovery(result, 100000);
        TestContext.WriteLine($"Target depth 100000; native cancellation at {checkpoint} visited/{capturedFrames} captured; " +
            $"frame bindings {walking.RetainedFrameBindings} -> {terminal.RetainedFrameBindings}; walk references 3 -> 0. " +
            $"Isolated host private bytes {result.GetProperty("privateBytesBefore")} -> {result.GetProperty("privateBytesAfter")}.");
    }

    /// <summary>
    /// Reports bounded traversal and truthful totals at the stack end and beyond it.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeStackProgressReportsBoundedPagesAndTotals()
    {
        using JsonDocument document = await RunProbeAsync("observe", 0, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        DebugStackWalkProgress[] updates = ReadUpdates(result);
        Assert.AreSequenceEqual(s_pageCheckpoints, updates.Select(static value => value.InspectedFrames));
        Assert.IsTrue(updates[..^1].All(static value => value.State == DebugStackWalkState.Walking && value.OwnedWalkInterfaces == 3));
        Assert.AreEqual(DebugStackWalkState.Completed, updates[^1].State);
        Assert.AreEqual(0, updates[^1].OwnedWalkInterfaces);
        Assert.AreEqual(1000, updates[^1].RetainedFrameBindings);
        Assert.AreEqual(1000, result.GetProperty("page").GetProperty("StackFrames").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("page").GetProperty("TotalFrames").ValueKind);
        JsonElement tail = result.GetProperty("tail");
        int total = tail.GetProperty("TotalFrames").GetInt32();
        Assert.AreEqual(5000 + tail.GetProperty("StackFrames").GetArrayLength(), total);
        Assert.IsLessThan(64, tail.GetProperty("StackFrames").GetArrayLength());
        Assert.AreEqual(total, result.GetProperty("tailProgress").GetProperty("InspectedFrames").GetInt32());
        Assert.AreEqual(total, result.GetProperty("empty").GetProperty("TotalFrames").GetInt32());
        Assert.AreEqual(0, result.GetProperty("empty").GetProperty("StackFrames").GetArrayLength());
        Assert.AreEqual(0, result.GetProperty("emptyProgress").GetProperty("CapturedFrames").GetInt32());
        Assert.AreEqual(0, result.GetProperty("emptyProgress").GetProperty("OwnedWalkInterfaces").GetInt32());
        Assert.AreEqual(result.GetProperty("tailProgress").GetProperty("RetainedFrameBindings").GetInt32(),
            result.GetProperty("emptyProgress").GetProperty("RetainedFrameBindings").GetInt32());
        AssertRecovery(result, 5000);
    }

    /// <summary>
    /// Reports rollback before failure notification, retaining both failures if the receiver also fails.
    /// </summary>
    /// <param name="mode">Whether failure reporting succeeds or its destination is closed.</param>
    [TestMethod]
    [DataRow("oversized")]
    [DataRow("fail-failed")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeStackProgressFailureReportsReleasedBindings(string mode)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.AreEqual(mode == "oversized" ? nameof(InvalidOperationException) : nameof(AggregateException),
            result.GetProperty("failureType").GetString());
        Assert.Contains("4096", result.GetProperty("failureMessage").GetString()!);
        if (mode == "fail-failed")
        {
            Assert.AreEqual(2, result.GetProperty("causes").GetArrayLength());
            Assert.AreEqual(nameof(InvalidOperationException), result.GetProperty("causes")[0].GetString());
            Assert.AreEqual(nameof(IOException), result.GetProperty("notificationCause").GetString());
        }
        DebugStackWalkProgress terminal = ReadUpdates(result)[^1];
        Assert.AreEqual(DebugStackWalkState.Failed, terminal.State);
        Assert.AreEqual(4097, terminal.InspectedFrames);
        Assert.AreEqual(4096, terminal.CapturedFrames);
        Assert.AreEqual(1, terminal.RetainedFrameBindings);
        Assert.AreEqual(0, terminal.OwnedWalkInterfaces);
        Assert.AreEqual(1, result.GetProperty("recovery").GetProperty("RetainedFrameBindings").GetInt32());
        AssertRecovery(result, 5000);
    }

    /// <summary>
    /// Releases unpublished bindings when an active or completed progress receiver fails.
    /// </summary>
    /// <param name="mode">The real client's failed progress behavior.</param>
    /// <param name="state">The notification that the client rejects.</param>
    /// <param name="innerType">The failure retained by the inspection error.</param>
    [TestMethod]
    [DataRow("fail-walking", DebugStackWalkState.Walking, nameof(IOException))]
    [DataRow("fail-completed", DebugStackWalkState.Completed, nameof(IOException))]
    [DataRow("fail-canceled", DebugStackWalkState.Walking, nameof(OperationCanceledException))]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeStackProgressReceiverFailureRollsBack(string mode, DebugStackWalkState state, string innerType)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.AreEqual(nameof(InvalidOperationException), result.GetProperty("failureType").GetString());
        Assert.AreEqual(innerType, result.GetProperty("innerType").GetString());
        Assert.AreEqual(state, ReadUpdates(result)[^1].State);
        Assert.AreEqual(1, result.GetProperty("recovery").GetProperty("RetainedFrameBindings").GetInt32());
        Assert.AreEqual(0, result.GetProperty("recovery").GetProperty("OwnedWalkInterfaces").GetInt32());
        AssertRecovery(result, 5000);
    }

    /// <summary>
    /// Avoids native work for an already-canceled request and preserves a page completed before cancellation.
    /// </summary>
    /// <param name="mode">The point at which the client cancels its request.</param>
    [TestMethod]
    [DataRow("pre-cancel")]
    [DataRow("cancel-completed")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StackCancellationBeforeStartAndAfterCompletionPreservesFrames(string mode)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        if (mode == "pre-cancel")
        {
            Assert.Contains("CanceledException", result.GetProperty("failureType").GetString()!);
            Assert.IsEmpty(ReadUpdates(result));
            Assert.AreEqual(1, result.GetProperty("recovery").GetProperty("RetainedFrameBindings").GetInt32());
        }
        else
        {
            Assert.IsFalse(result.TryGetProperty("failureType", out _));
            Assert.AreEqual(DebugStackWalkState.Completed, ReadUpdates(result)[^1].State);
            Assert.AreEqual(1000, result.GetProperty("page").GetProperty("StackFrames").GetArrayLength());
            Assert.AreEqual(1000, result.GetProperty("recovery").GetProperty("RetainedFrameBindings").GetInt32());
        }

        AssertRecovery(result, 5000);
    }

    private static DebugStackWalkProgress[] ReadUpdates(JsonElement result) =>
        result.GetProperty("updates").Deserialize<DebugStackWalkProgress[]>() ?? throw new InvalidDataException("No traversal updates.");

    private static void AssertRecovery(JsonElement result, int depth)
    {
        Assert.AreEqual(depth, result.GetProperty("depth").GetInt32());
        Assert.AreEqual($"depth:{depth.ToString(CultureInfo.InvariantCulture)}", result.GetProperty("output").GetString());
        Assert.AreEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("breakpoint", result.GetProperty("stopped").GetProperty("StopReason").GetString());
        Assert.AreEqual(result.GetProperty("stopped").GetProperty("StopGeneration").GetInt64(),
            result.GetProperty("unchanged").GetProperty("StopGeneration").GetInt64());
        Assert.AreEqual((int)DebugSessionState.Stopped, result.GetProperty("unchanged").GetProperty("State").GetInt32());
        JsonElement original = result.GetProperty("top").GetProperty("StackFrames")[0];
        JsonElement refreshed = result.GetProperty("refreshed").GetProperty("StackFrames")[0];
        Assert.AreEqual(original.GetProperty("Id").GetInt32(), refreshed.GetProperty("Id").GetInt32());
        Assert.AreEqual(original.GetProperty("InstructionReference").GetString(), refreshed.GetProperty("InstructionReference").GetString());
        foreach (string property in new[] { "initialArguments", "afterArguments", "deepArguments" })
        {
            JsonElement arguments = result.GetProperty(property);
            Assert.AreEqual((property == "deepArguments" ? depth - 1 : 0).ToString(CultureInfo.InvariantCulture),
                arguments.EnumerateArray().Single(static value => value.GetProperty("Name").GetString() == "remaining").GetProperty("Value").GetString());
            Assert.AreEqual((property == "deepArguments" ? 1 : depth).ToString(CultureInfo.InvariantCulture),
                arguments.EnumerateArray().Single(static value => value.GetProperty("Name").GetString() == "entered").GetProperty("Value").GetString());
        }
    }

    private async Task<JsonDocument> RunProbeAsync(string mode, int offset, int checkpoint)
    {
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
            ?? Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
        ProcessStartInfo startInfo = new("dotnet") { WorkingDirectory = root };
        startInfo.ArgumentList.Add(Path.Join(root, "artifacts", "bin", "Csls.Debugger.StackProbe", "debug", "csls-debugger-stack-probe.dll"));
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(offset.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(checkpoint.ToString(CultureInfo.InvariantCulture));
        DebuggerWorkerEnvironment.Configure(startInfo, worker);
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(startInfo, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, error);
        Assert.IsEmpty(error);
        return JsonDocument.Parse(output);
    }
}
