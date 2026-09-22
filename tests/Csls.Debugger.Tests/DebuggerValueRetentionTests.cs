using System.Diagnostics;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies retained-value ownership through a real debugger and target process.
/// </summary>
[TestClass]
public sealed class DebuggerValueRetentionTests
{
    /// <summary>
    /// Gets the cancellation and diagnostics owned by the current test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Keeps native value and memory-handle counts stable across repeated stopped-state array reads.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RepeatedArrayPagesKeepRetentionBounded()
    {
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string probe = Path.Join(root, "artifacts", "bin", "Csls.Debugger.StackProbe", "debug",
            "csls-debugger-stack-probe.dll");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("values-repeat");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("0");
        string worker = Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug",
            "csls-debugger-worker.dll");
        DebuggerWorkerEnvironment.Configure(start, worker);

        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            start, TestContext.CancellationToken, diagnosticContext: TestContext).ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, error);
        using var report = JsonDocument.Parse(output);
        JsonElement result = report.RootElement;
        JsonElement first = result.GetProperty("firstProgress");
        JsonElement last = result.GetProperty("lastProgress");
        Assert.AreEqual(64, first.GetProperty("PageValues").GetInt32());
        Assert.AreEqual(64, last.GetProperty("PageValues").GetInt32());
        Assert.IsGreaterThanOrEqualTo(64, first.GetProperty("RetainedValues").GetInt32());
        Assert.IsGreaterThanOrEqualTo(64, first.GetProperty("RetainedMemoryReferences").GetInt32());
        Assert.AreEqual(first.GetProperty("RetainedValues").GetInt32(),
            last.GetProperty("RetainedValues").GetInt32());
        Assert.AreEqual(first.GetProperty("RetainedMemoryReferences").GetInt32(),
            last.GetProperty("RetainedMemoryReferences").GetInt32());

        int[] firstReferences = [.. result.GetProperty("firstPage").EnumerateArray()
            .Select(value => value.GetProperty("VariablesReference").GetInt32())];
        int[] lastReferences = [.. result.GetProperty("lastPage").EnumerateArray()
            .Select(value => value.GetProperty("VariablesReference").GetInt32())];
        Assert.HasCount(64, firstReferences);
        Assert.HasCount(64, firstReferences.Distinct().ToArray());
        Assert.IsTrue(firstReferences.All(reference => reference > 0));
        Assert.AreSequenceEqual(firstReferences, lastReferences);
    }
}
