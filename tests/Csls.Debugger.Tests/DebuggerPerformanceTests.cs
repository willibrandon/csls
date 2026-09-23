using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger measurements through the real harness, DAP adapter, target processes, and report files.
/// </summary>
[TestClass]
public sealed class DebuggerPerformanceTests : DapTestContext
{
    /// <summary>
    /// Records verified operations, phase-separated samples, executable identities, and independently observed resources.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasurementsPreserveOperationsAndProcessOwnership()
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-performance-").FullName;
        try
        {
            string path = Path.Join(directory, "measurement.json");
            (int exit, string output, string error) = await RunHarnessAsync(path, ["--iterations", "2", "--samples", "2"])
                .ConfigureAwait(false);
            Assert.AreEqual(0, exit, error);
            Assert.Contains(path, output);
            using JsonDocument document = await ReadReportAsync(path).ConfigureAwait(false);
            JsonElement report = document.RootElement;
            Assert.AreEqual(1, report.GetProperty("schemaVersion").GetInt32());
            Assert.IsTrue(report.GetProperty("passed").GetBoolean());
            Assert.AreEqual(0, report.GetProperty("budgetViolations").GetArrayLength());
            Assert.AreEqual(LauncherPath, report.GetProperty("serverPath").GetString());
            Assert.AreEqual(HarnessPath, report.GetProperty("targetPath").GetString());
            Assert.AreEqual(SourcePath, report.GetProperty("sourcePath").GetString());
            Assert.AreEqual(await HashAsync(LauncherPath).ConfigureAwait(false), report.GetProperty("serverSha256").GetString());
            Assert.AreEqual(await HashAsync(HarnessPath).ConfigureAwait(false), report.GetProperty("targetSha256").GetString());
            JsonElement worker = Assert.ContainsSingle(report.GetProperty("workerOverrides").EnumerateArray()
                .Where(item => item.GetProperty("variable").GetString() == "CSLS_DEBUGGER_WORKER_PATH"));
            Assert.AreEqual(WorkerPath, worker.GetProperty("path").GetString());
            Assert.AreEqual(await HashAsync(WorkerPath).ConfigureAwait(false), worker.GetProperty("sha256").GetString());
            JsonElement configuration = report.GetProperty("configuration");
            Assert.AreEqual(2, configuration.GetProperty("iterations").GetInt32());
            Assert.AreEqual(2, configuration.GetProperty("samples").GetInt32());
            Assert.AreEqual(10000d, configuration.GetProperty("operationBudgetMilliseconds").GetDouble());
            JsonElement environment = report.GetProperty("environment");
            Assert.IsGreaterThan(0, environment.GetProperty("processorCount").GetInt32());
            string? runtime = environment.GetProperty("runtimeIdentifier").GetString();
            string? sdk = environment.GetProperty("dotNetSdkVersion").GetString();
            Assert.IsNotNull(runtime);
            Assert.IsNotNull(sdk);
            Assert.IsNotEmpty(runtime);
            Assert.IsNotEmpty(sdk);

            JsonElement measurements = report.GetProperty("measurements");
            Assert.AreEqual(2, measurements.GetArrayLength());
            for (int iteration = 0; iteration < measurements.GetArrayLength(); iteration++)
            {
                JsonElement measurement = measurements[iteration];
                Assert.AreEqual(iteration + 1, measurement.GetProperty("iteration").GetInt32());
                Assert.AreEqual(iteration == 0 ? "first-process" : "subsequent-process",
                    measurement.GetProperty("processState").GetString());
                int adapterId = measurement.GetProperty("adapterProcessId").GetInt32();
                int targetId = measurement.GetProperty("targetProcessId").GetInt32();
                Assert.IsGreaterThan(0, adapterId);
                Assert.IsGreaterThan(0, targetId);
                Assert.AreNotEqual(adapterId, targetId);
                Assert.AreEqual(0, measurement.GetProperty("targetExitCode").GetInt32());
                Assert.AreEqual(0, measurement.GetProperty("adapterExitCode").GetInt32());
                string? targetOutput = measurement.GetProperty("output").GetString();
                Assert.IsNotNull(targetOutput);
                Assert.AreEqual("debugger-performance-output:42:256", targetOutput.Trim());
                JsonElement checkpoints = measurement.GetProperty("resources");
                Assert.AreEqual(2, checkpoints.GetArrayLength());
                Assert.AreEqual("stopped-before-inspection", checkpoints[0].GetProperty("phase").GetString());
                Assert.AreEqual("stopped-after-inspection", checkpoints[1].GetProperty("phase").GetString());
                foreach (JsonElement checkpoint in checkpoints.EnumerateArray())
                {
                    int[] ids = [.. checkpoint.GetProperty("processIds").EnumerateArray().Select(item => item.GetInt32())];
                    Assert.Contains(adapterId, ids);
                    Assert.Contains(targetId, ids);
                    Assert.IsGreaterThan(0L, checkpoint.GetProperty("workingSetBytes").GetInt64());
                    ValidatePrivateMemory(checkpoint.GetProperty("privateMemoryBytes"));
                    Assert.IsGreaterThanOrEqualTo(0d, checkpoint.GetProperty("processorTimeMilliseconds").GetDouble());
                }
                ValidateSamples(measurement.GetProperty("samples"), repeatedCount: 2);
                await AssertOwnedProcessExitedAsync(adapterId, measurement.GetProperty("adapterStartedAtUtc").GetDateTimeOffset()).ConfigureAwait(false);
                await AssertOwnedProcessExitedAsync(targetId, measurement.GetProperty("targetStartedAtUtc").GetDateTimeOffset()).ConfigureAwait(false);
            }
            ValidateSummary(report);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects invalid measurement options before publishing a report.
    /// </summary>
    /// <param name="option">The option under validation.</param>
    /// <param name="value">The invalid value passed through the real command line.</param>
    [TestMethod]
    [DataRow("--iterations", "0")]
    [DataRow("--iterations", "-1")]
    [DataRow("--samples", "0")]
    [DataRow("--samples", "-1")]
    [DataRow("--timeout-seconds", "0")]
    [DataRow("--timeout-seconds", "-1")]
    [DataRow("--operation-budget-ms", "0")]
    [DataRow("--operation-budget-ms", "-1")]
    [DataRow("--operation-budget-ms", "NaN")]
    [DataRow("--operation-budget-ms", "Infinity")]
    [DataRow("--server", "missing")]
    [DataRow("--target", "missing")]
    [DataRow("--fixture-source", "missing")]
    [DataRow("--fixture-source", "wrong-marker")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidArgumentsDoNotPublishResults(string option, string value)
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-performance-invalid-").FullName;
        try
        {
            string path = Path.Join(directory, "measurement.json");
            string argument = value switch
            {
                "missing" => Path.Join(directory, "absent.file"),
                "wrong-marker" => Path.Join(FindRepositoryRoot(), "benchmarks", "Csls.EndToEndPerformance", "PerformanceOperation.cs"),
                _ => value
            };
            (int exit, string _, string error) = await RunHarnessAsync(path, [option, argument]).ConfigureAwait(false);
            Assert.AreNotEqual(0, exit);
            Assert.IsNotEmpty(error);
            Assert.IsFalse(File.Exists(path));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finishes real measurements and preserves their report when the requested latency budget is exceeded.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task BudgetFailureRetainsMeasurements()
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-performance-budget-").FullName;
        try
        {
            string path = Path.Join(directory, "measurement.json");
            (int exit, string _, string error) = await RunHarnessAsync(path,
                ["--operation-budget-ms", "0.000001"]).ConfigureAwait(false);
            Assert.AreEqual(1, exit, error);
            using JsonDocument document = await ReadReportAsync(path).ConfigureAwait(false);
            JsonElement report = document.RootElement;
            Assert.IsFalse(report.GetProperty("passed").GetBoolean());
            Assert.AreEqual(1, report.GetProperty("measurements").GetArrayLength());
            ValidateSamples(report.GetProperty("measurements")[0].GetProperty("samples"), repeatedCount: 1);
            Assert.AreEqual(17, report.GetProperty("budgetViolations").GetArrayLength());
            foreach (string? message in report.GetProperty("budgetViolations").EnumerateArray().Select(violation => violation.GetString()))
            {
                Assert.IsNotNull(message);
                Assert.Contains(message, error);
            }
            ValidateSummary(report);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserves an existing report and refuses to overwrite its content.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExistingReportIsPreserved()
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-performance-existing-").FullName;
        try
        {
            string path = Path.Join(directory, "measurement.json");
            await File.WriteAllTextAsync(path, "retained report", TestContext.CancellationToken).ConfigureAwait(false);
            (int exit, string _, string error) = await RunHarnessAsync(path, []).ConfigureAwait(false);
            Assert.AreEqual(1, exit);
            Assert.Contains("already exists", error);
            Assert.AreEqual("retained report", await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.HasCount(1, Directory.EnumerateFiles(directory));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports actual executable and target startup failures without publishing successful measurements.
    /// </summary>
    /// <param name="scenario">The executable or target startup failure exercised through the command line.</param>
    [TestMethod]
    [DataRow("target")]
    [DataRow("server")]
    [DataRow("non-executable")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FailedLaunchDoesNotPublishResults(string scenario)
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-performance-launch-").FullName;
        try
        {
            string path = Path.Join(directory, "measurement.json");
            (int exit, string _, string error) = await RunHarnessAsync(path,
                [scenario == "target" ? "--target" : "--server",
                    scenario == "non-executable" ? SourcePath : ResolveTestProcessHost()]).ConfigureAwait(false);
            Assert.AreEqual(1, exit, error);
            Assert.Contains("Debugger measurement failed", error);
            Assert.IsFalse(File.Exists(path));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void ValidateSamples(JsonElement samples, int repeatedCount)
    {
        string[] sessionNames = ["dap/startup-initialize", "dap/setBreakpoints", "dap/launch-to-breakpoint",
            "dap/continue-output-exit", "dap/shutdown"];
        string[] requestNames = ["dap/threads", "dap/stackTrace", "dap/scopes", "dap/variables",
            "dap/variables/indexed-page", "dap/evaluate"];
        Assert.AreEqual(5 + 6 * (repeatedCount + 1), samples.GetArrayLength());
        foreach (string name in sessionNames)
        {
            JsonElement sample = Assert.ContainsSingle(samples.EnumerateArray().Where(item => item.GetProperty("name").GetString() == name));
            Assert.AreEqual("session", sample.GetProperty("phase").GetString());
        }
        foreach (string name in requestNames)
        {
            JsonElement[] matching = [.. samples.EnumerateArray().Where(item => item.GetProperty("name").GetString() == name)];
            Assert.HasCount(repeatedCount + 1, matching);
            Assert.AreEqual("first-request", matching[0].GetProperty("phase").GetString());
            foreach (JsonElement sample in matching.Skip(1))
            {
                Assert.AreEqual("repeated-request", sample.GetProperty("phase").GetString());
            }
        }
        foreach (double elapsed in samples.EnumerateArray().Select(sample => sample.GetProperty("milliseconds").GetDouble()))
        {
            Assert.IsTrue(double.IsFinite(elapsed));
            Assert.IsGreaterThan(0d, elapsed);
        }
    }

    private static void ValidateSummary(JsonElement report)
    {
        JsonElement summary = report.GetProperty("summary");
        Assert.AreEqual(17, summary.GetArrayLength());
        foreach (JsonElement operation in summary.EnumerateArray())
        {
            double[] durations = [.. report.GetProperty("measurements").EnumerateArray()
                .SelectMany(iteration => iteration.GetProperty("samples").EnumerateArray())
                .Where(sample => sample.GetProperty("name").GetString() == operation.GetProperty("name").GetString() &&
                    sample.GetProperty("phase").GetString() == operation.GetProperty("phase").GetString())
                .Select(sample => sample.GetProperty("milliseconds").GetDouble()).Order()];
            Assert.AreEqual(durations.Length, operation.GetProperty("sampleCount").GetInt32());
            double median = (durations[(durations.Length - 1) / 2] + durations[durations.Length / 2]) / 2;
            Assert.AreEqual(median, operation.GetProperty("medianMilliseconds").GetDouble());
            Assert.AreEqual(durations.Max(), operation.GetProperty("maximumMilliseconds").GetDouble());
        }
    }

    private static void ValidatePrivateMemory(JsonElement memory)
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.AreEqual(JsonValueKind.Null, memory.ValueKind);
        }
        else
        {
            Assert.IsGreaterThan(0L, memory.GetInt64());
        }
    }

    private Task<(int ExitCode, string Output, string Error)> RunHarnessAsync(string output, IReadOnlyList<string> overrides)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--server"] = LauncherPath,
            ["--fixture-source"] = SourcePath,
            ["--output"] = output,
            ["--iterations"] = "1",
            ["--samples"] = "1"
        };
        for (int index = 0; index < overrides.Count; index += 2)
        {
            arguments[overrides[index]] = overrides[index + 1];
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = FindRepositoryRoot()
        };
        start.ArgumentList.Add(HarnessPath);
        start.ArgumentList.Add("debugger");
        foreach ((string name, string value) in arguments)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
        start.Environment["CSLS_DEBUGGER_WORKER_PATH"] = WorkerPath;
        return DebuggerTestProcess.RunAsync(start, TestContext.CancellationToken);
    }

    private async Task<JsonDocument> ReadReportAsync(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task<string> HashAsync(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.CancellationToken).ConfigureAwait(false));
    }

    private async Task AssertOwnedProcessExitedAsync(int processId, DateTimeOffset startedAtUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        {
            if (process.StartTime.ToUniversalTime() == startedAtUtc)
            {
                await DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(process.HasExited);
            }
        }
    }

    private static string HarnessPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.EndToEndPerformance",
        "debug", "Csls.EndToEndPerformance.dll");

    private static string LauncherPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.App", "debug", "csls.dll");

    private static string WorkerPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");

    private static string SourcePath => Path.Join(FindRepositoryRoot(), "benchmarks", "Csls.EndToEndPerformance", "DebuggerPerformanceTarget.cs");
}
