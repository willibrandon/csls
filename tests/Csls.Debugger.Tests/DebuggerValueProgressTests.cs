using Csls.Debugger.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation, progress failure, and value ownership through a real engine process.
/// </summary>
[TestClass]
public sealed class DebuggerValueProgressTests
{
    private static readonly int[] s_pageCheckpoints = [256, 512, 768, 1000];

    /// <summary>
    /// Gets the framework-owned cancellation and evidence context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancels at an actual value-read checkpoint and reuses published references after rollback.
    /// </summary>
    /// <param name="checkpoint">The exact formatted-value count at which the client cancels.</param>
    [TestMethod]
    [DataRow(256)]
    [DataRow(512)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledLiveValuePagesReleaseUnpublishedBindings(int checkpoint)
    {
        using JsonDocument document = await RunProbeAsync("cancel", checkpoint).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.IsTrue(result.TryGetProperty("failureType", out JsonElement failureType),
            "The engine completed a page after its client canceled at a value-read checkpoint.");
        Assert.AreEqual("TaskCanceledException", failureType.GetString());
        Assert.IsTrue(result.GetProperty("cancellationMatches").GetBoolean());
        Assert.IsFalse(result.TryGetProperty("page", out _));
        DebugValueReadProgress[] updates = ReadUpdates(result);
        Assert.HasCount(checkpoint / 256 + 1, updates);
        Assert.AreEqual(checkpoint, updates[^1].FormattedValues);
        Assert.AreEqual(DebugValueReadState.Canceled, updates[^1].State);
        Assert.AreEqual(0, updates[^1].PageValues);
        AssertRestoredBindings(result, updates[^1]);
        AssertRecovery(result);
    }

    /// <summary>
    /// Delivers bounded page counts and treats cancellation by a completed-page observer as completion.
    /// </summary>
    /// <param name="mode">Whether completion triggers late client cancellation.</param>
    [TestMethod]
    [DataRow("observe")]
    [DataRow("cancel-completed")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CompletedLiveValuePagesReportBoundedProgress(string mode)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.IsFalse(result.TryGetProperty("failureType", out _));
        Assert.AreEqual(1000, result.GetProperty("page").GetArrayLength());
        DebugValueReadProgress[] updates = ReadUpdates(result);
        Assert.AreSequenceEqual(s_pageCheckpoints, updates.Select(static value => value.FormattedValues));
        foreach (DebugValueReadProgress update in updates[..^1])
        {
            Assert.AreEqual(DebugValueReadState.Reading, update.State);
            Assert.AreEqual(0, update.PageValues);
            Assert.AreEqual(result.GetProperty("reference").GetInt32(), update.VariablesReference);
        }
        Assert.AreEqual(DebugValueReadState.Completed, updates[^1].State);
        Assert.AreEqual(1000, updates[^1].PageValues);
        AssertRecovery(result);
    }

    /// <summary>
    /// Observes an empty page or a queued cancellation without formatting or retaining new values.
    /// </summary>
    /// <param name="mode">The empty request or pre-canceled request to issue.</param>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("pre-cancel")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EmptyAndPreCanceledLivePagesPreserveBindings(string mode)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        if (mode == "pre-cancel")
        {
            Assert.IsEmpty(ReadUpdates(result));
            Assert.IsTrue(result.GetProperty("cancellationMatches").GetBoolean());
            Assert.IsFalse(result.TryGetProperty("page", out _));
        }
        else
        {
            Assert.AreEqual(0, result.GetProperty("page").GetArrayLength());
            DebugValueReadProgress terminal = Assert.ContainsSingle(ReadUpdates(result));
            Assert.AreEqual(DebugValueReadState.Completed, terminal.State);
            Assert.AreEqual(0, terminal.FormattedValues);
            Assert.AreEqual(0, terminal.PageValues);
            AssertRestoredBindings(result, terminal);
        }
        AssertRestoredBindings(result, ReadProgress(result, "recovery"));
        AssertRecovery(result);
    }

    /// <summary>
    /// Rolls back unpublished values when the requesting progress receiver throws.
    /// </summary>
    /// <param name="mode">The receiver failure location and exception kind.</param>
    [TestMethod]
    [DataRow("fail-reading")]
    [DataRow("fail-completed")]
    [DataRow("unexpected")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LiveValueProgressReceiverFailureReleasesBindings(string mode)
    {
        using JsonDocument document = await RunProbeAsync(mode, 0).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.AreEqual(mode == "unexpected" ? "FormatException" : "InvalidOperationException",
            result.GetProperty("failureType").GetString());
        Assert.AreEqual(mode == "unexpected" ? null : "IOException", result.GetProperty("innerType").GetString());
        Assert.IsFalse(result.TryGetProperty("page", out _));
        DebugValueReadProgress[] updates = ReadUpdates(result);
        Assert.HasCount(mode == "fail-completed" ? 4 : 1, updates);
        Assert.AreEqual(mode == "fail-completed" ? 1000 : 256, updates[^1].FormattedValues);
        AssertRestoredBindings(result, ReadProgress(result, "recovery"));
        AssertRecovery(result);
    }

    /// <summary>
    /// Preserves the original inspection error alongside a failing terminal notification.
    /// </summary>
    /// <param name="mode">Whether the original error is a page limit or cancellation.</param>
    /// <param name="checkpoint">The exact client cancellation checkpoint, or zero for a page limit.</param>
    [TestMethod]
    [DataRow("fail-failed", 0)]
    [DataRow("fail-canceled", 256)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LiveValueFailureNotificationPreservesBothCauses(string mode, int checkpoint)
    {
        using JsonDocument document = await RunProbeAsync(mode, checkpoint).ConfigureAwait(false);
        JsonElement result = document.RootElement;
        Assert.AreEqual("AggregateException", result.GetProperty("failureType").GetString());
        Assert.AreSequenceEqual(new[] { mode == "fail-failed" ? "InvalidOperationException" : "OperationCanceledException",
            "InvalidOperationException" }, result.GetProperty("causes").EnumerateArray().Select(static value => value.GetString()));
        Assert.AreEqual("IOException", result.GetProperty("notificationCause").GetString());
        DebugValueReadProgress terminal = ReadUpdates(result)[^1];
        Assert.AreEqual(mode == "fail-failed" ? DebugValueReadState.Failed : DebugValueReadState.Canceled, terminal.State);
        Assert.AreEqual(checkpoint, terminal.FormattedValues);
        AssertRestoredBindings(result, terminal);
        AssertRecovery(result);
    }

    private static DebugValueReadProgress[] ReadUpdates(JsonElement result) =>
        result.GetProperty("updates").Deserialize<DebugValueReadProgress[]>() ?? throw new InvalidDataException("Missing value progress.");

    private static DebugValueReadProgress ReadProgress(JsonElement result, string property) =>
        result.GetProperty(property).Deserialize<DebugValueReadProgress>() ?? throw new InvalidDataException("Missing value snapshot.");

    private static void AssertRestoredBindings(JsonElement result, DebugValueReadProgress restored)
    {
        DebugValueReadProgress baseline = ReadProgress(result, "baseline");
        Assert.IsGreaterThan(0, baseline.RetainedValues);
        Assert.IsGreaterThan(0, baseline.RetainedMemoryReferences);
        Assert.AreEqual(baseline.RetainedValues, restored.RetainedValues);
        Assert.AreEqual(baseline.RetainedMemoryReferences, restored.RetainedMemoryReferences);
    }

    private static void AssertRecovery(JsonElement result)
    {
        Assert.AreEqual(result.GetProperty("initial").GetRawText(), result.GetProperty("originalChild").GetRawText());
        Assert.AreEqual("41", result.GetProperty("originalChild")[0].GetProperty("Value").GetString());
        Assert.AreEqual("[65536]", result.GetProperty("tail")[0].GetProperty("Name").GetString());
        Assert.AreEqual("41", result.GetProperty("tailChild")[0].GetProperty("Value").GetString());
        Assert.AreEqual(result.GetProperty("stopped").GetProperty("StopGeneration").GetInt64(),
            result.GetProperty("unchanged").GetProperty("StopGeneration").GetInt64());
        Assert.AreEqual((int)DebugSessionState.Stopped, result.GetProperty("unchanged").GetProperty("State").GetInt32());
        JsonElement original = result.GetProperty("stack").GetProperty("StackFrames")[0];
        JsonElement refreshed = result.GetProperty("refreshed").GetProperty("StackFrames")[0];
        Assert.AreEqual(original.GetProperty("Id").GetInt32(), refreshed.GetProperty("Id").GetInt32());
        Assert.AreEqual(original.GetProperty("InstructionReference").GetString(), refreshed.GetProperty("InstructionReference").GetString());
        Assert.AreEqual((int)DebugSessionState.Terminated, result.GetProperty("terminated").GetProperty("State").GetInt32());
    }

    private async Task<JsonDocument> RunProbeAsync(string mode, int checkpoint)
    {
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
            ?? Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
        ProcessStartInfo startInfo = new("dotnet") { WorkingDirectory = root };
        startInfo.ArgumentList.Add(Path.Join(root, "artifacts", "bin", "Csls.Debugger.StackProbe", "debug", "csls-debugger-stack-probe.dll"));
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("values-" + mode);
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add(checkpoint.ToString(CultureInfo.InvariantCulture));
        DebuggerWorkerEnvironment.Configure(startInfo, worker);
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(startInfo, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, error);
        Assert.IsEmpty(error);
        return JsonDocument.Parse(output);
    }
}
