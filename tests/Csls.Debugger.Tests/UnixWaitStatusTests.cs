using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies the packaged Unix interposer through the real runtime's native wait consumer.
/// </summary>
[TestClass]
public sealed class UnixWaitStatusTests
{
    /// <summary>
    /// Gets or sets the framework context for cancellation and diagnostics.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves exact exit status for native consumers after the sole owner reaps the child.
    /// </summary>
    /// <param name="exitCode">The actual child process's requested exit code.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(23)]
    [DataRow(255)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeRuntimeConsumerObservesRetainedChildStatus(int exitCode)
    {
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
            ?? Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = root
        };
        startInfo.ArgumentList.Add(Path.Join(
            root, "artifacts", "bin", "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"));
        startInfo.ArgumentList.Add("--unix-wait-status-fixture");
        startInfo.ArgumentList.Add(exitCode.ToString(CultureInfo.InvariantCulture));
        DebuggerWorkerEnvironment.Configure(startInfo, worker);

        (int ExitCode, string Output, string Error) result = await DebuggerTestProcess.RunAsync(startInfo, TestContext.CancellationToken)
            .ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.IsEmpty(result.Error);
        string[] fields = result.Output.Trim().Split(',');
        Assert.HasCount(7, fields, result.Output);
        int[] observations = [.. fields.Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
        int processId = observations[0];
        Assert.IsGreaterThan(0, processId);
        Assert.AreEqual(processId, observations[1], "The sole owner must reap its direct child.");
        Assert.AreEqual(exitCode << 8, observations[2], "The owner must retain the exact native wait status.");
        Assert.AreEqual(processId, observations[3], $"The native runtime must observe the retained child: {result.Output}");
        Assert.AreEqual(exitCode, observations[4], "The runtime must decode the original exit code.");
        Assert.AreEqual(0, observations[5], "Normal child exit must not be reported as signal termination.");
    }
}
