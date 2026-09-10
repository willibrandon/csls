using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures bounded platform diagnostics from a failed test's owned process tree.
/// </summary>
internal static class DebuggerProcessDiagnostics
{
    private const int MaximumProcesses = 8;
    private static readonly SemaphoreSlim s_fileActivityCaptureGate = new(1, 1);

    /// <summary>
    /// Retains kernel wait reports for the owned adapter and its reported target before failure cleanup.
    /// </summary>
    /// <param name="client">The live DAP client retaining ownership of the adapter and target.</param>
    /// <param name="testContext">The test context retaining the reports.</param>
    /// <returns>Completion after read-only observation or its bounded diagnostic deadline.</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static async Task CaptureLinuxWaitStatesAsync(DapTestClient client, TestContext testContext)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string directory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results",
            $"kernel-waits-{client.HostProcessId}-{Guid.NewGuid():N}");
        int[] processes = client.TargetProcessId is int target
            ? [client.HostProcessId, target] : [client.HostProcessId];
        try
        {
            Directory.CreateDirectory(directory);
            foreach (int processId in processes)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    _ = process.SafeHandle;
                    string report = await LinuxDebuggerProcessCapture.CaptureKernelStateAsync(process, directory,
                        cancellation.Token).ConfigureAwait(false);
                    testContext.WriteLine($"Kernel wait capture completed: {report}.");
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or
                    UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
                {
                    testContext.WriteLine($"Kernel wait capture for {processId}: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            testContext.WriteLine($"Kernel wait capture: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string artifact in Directory.EnumerateFiles(directory))
                {
                    testContext.AddResultFile(artifact);
                }
            }
        }
    }

    /// <summary>
    /// Records native wait locations before a failed test releases its owned processes.
    /// </summary>
    /// <param name="hostProcessId">The root process owned by the test.</param>
    /// <param name="testContext">The test context that retains diagnostic artifact paths.</param>
    /// <returns>A task that completes after diagnostic collection or its bounded deadline.</returns>
    internal static async Task CaptureAsync(int hostProcessId, TestContext testContext)
    {
        if (OperatingSystem.IsWindows())
        {
            await WindowsDebuggerProcessCapture.CaptureTreeAsync(hostProcessId, testContext).ConfigureAwait(false);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            await CaptureLinuxWorkerAsync(hostProcessId, testContext).ConfigureAwait(false);
            return;
        }
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            testContext.WriteLine($"Inspecting descendants of owned process {hostProcessId}.");
            var startInfo = new ProcessStartInfo("/bin/ps");
            startInfo.ArgumentList.Add("-axo");
            startInfo.ArgumentList.Add("pid=,ppid=");
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, cancellation.Token).ConfigureAwait(false);
            testContext.WriteLine($"Process inspection completed with exit code {exitCode}.");
            if (exitCode != 0)
            {
                testContext.WriteLine($"Process inspection exited with {exitCode}: {error}");
                return;
            }

            var parents = new Dictionary<int, int>();
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length == 2 &&
                    int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) &&
                    int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId))
                {
                    parents[processId] = parentId;
                }
            }

            if (!parents.ContainsKey(hostProcessId))
            {
                testContext.WriteLine($"Owned process {hostProcessId} exited before native stack capture.");
                return;
            }

            var processes = new List<int> { hostProcessId };
            for (int index = 0; index < processes.Count && processes.Count < MaximumProcesses; index++)
            {
                foreach ((int processId, int parentId) in parents)
                {
                    if (parentId == processes[index] && !processes.Contains(processId))
                    {
                        processes.Add(processId);
                        if (processes.Count == MaximumProcesses)
                        {
                            break;
                        }
                    }
                }
            }

            string directory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
                "artifacts", "test-results", $"native-stacks-{hostProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            testContext.WriteLine($"Capturing owned process stacks: {string.Join(", ", processes)}.");
            await CaptureWaitStatesAsync(processes, testContext, cancellation.Token).ConfigureAwait(false);
            // Observe filesystem timing before stack profilers suspend the captured processes.
            await CaptureFileActivityAsync(processes, directory, testContext, cancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(processes.SelectMany(processId => new[]
            {
                CaptureProcessAsync(processId, directory, testContext, cancellation.Token),
                CaptureManagedProcessAsync(processId, directory, testContext, cancellation.Token)
            }).Append(CaptureKernelStacksAsync(processes, directory, testContext, cancellation.Token))
                .Append(DebuggerMacAuthorizationDiagnostics.CaptureAsync(testContext, cancellation.Token)))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            testContext.WriteLine($"Native stack capture: {exception.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task CaptureLinuxWorkerAsync(int hostProcessId, TestContext testContext)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string repository = DebuggerTestEnvironment.FindRepositoryRoot();
        string directory = Path.Join(repository, "artifacts", "test-results",
            $"native-stacks-{hostProcessId}-{Guid.NewGuid():N}");
        try
        {
            string workerPath = Path.Join(repository, "artifacts", "bin", "Csls.Debugger.Worker", "debug",
                "csls-debugger-worker.dll");
            using Process worker = LinuxDebuggerProcessTree.OpenWorker(hostProcessId, workerPath);
            Directory.CreateDirectory(directory);
            testContext.WriteLine($"Capturing owned Linux worker {worker.Id} for adapter {hostProcessId}.");
            string dump = await LinuxDebuggerProcessCapture.CaptureAsync(worker, directory, cancellation.Token)
                .ConfigureAwait(false);
            testContext.WriteLine($"Native worker capture completed: {dump}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or DiagnosticsClientException or InvalidOperationException or Win32Exception)
        {
            testContext.WriteLine($"Linux worker capture: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string artifact in Directory.EnumerateFiles(directory))
                {
                    testContext.AddResultFile(artifact);
                }
            }
        }
    }

    private static async Task CaptureWaitStatesAsync(
        List<int> processes, TestContext testContext, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("/bin/ps");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(string.Join(",", processes));
        startInfo.ArgumentList.Add("-o");
        // Record elapsed and cumulative CPU time before the profilers suspend or otherwise observe the target.
        startInfo.ArgumentList.Add("pid,ppid,state,wchan,pcpu,time,etime,rss,vsz,comm");
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo, cancellationToken).ConfigureAwait(false);
        testContext.WriteLine($"Owned process wait states and resource use (exit {exitCode}): {output}{error}");
    }

    private static async Task CaptureKernelStacksAsync(
        List<int> processes, string directory, TestContext testContext, CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, "owned-processes.spindump.txt");
        bool hostedRunner = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(hostedRunner ? "/usr/bin/sudo" : "/usr/sbin/spindump");
        if (hostedRunner)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("/usr/sbin/spindump");
        }

        startInfo.ArgumentList.Add(processes[0].ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("10");
        // Spindump otherwise samples the whole host. Restrict it to this test's owned process tree.
        startInfo.ArgumentList.Add("-onlyTarget");
        foreach (int processId in processes.Skip(1))
        {
            startInfo.ArgumentList.Add("-proc");
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-timeline");
        startInfo.ArgumentList.Add("-noBinary");
        startInfo.ArgumentList.Add("-timelimit");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(path);
        testContext.WriteLine($"Capturing owned process kernel stacks into {path}.");
        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Owned process kernel stack capture exited with {exitCode}: {output}{error}");
        }
        finally
        {
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
    }

    private static async Task CaptureFileActivityAsync(
        List<int> processes, string directory, TestContext testContext, CancellationToken cancellationToken)
    {
        bool hostedRunner = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(hostedRunner ? "/usr/bin/sudo" : "/usr/bin/fs_usage");
        if (hostedRunner)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("/usr/bin/fs_usage");
        }
        startInfo.ArgumentList.Add("-w");
        // Scheduler transitions are separately enabled so syscall durations can identify time scheduled out.
        startInfo.ArgumentList.Add("-W");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("filesys");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("1");
        // An explicit inclusion list keeps unrelated host filesystem activity out of the report.
        foreach (int processId in processes)
        {
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        }

        string path = Path.Join(directory, "owned-processes.filesystem.txt");
        int exitCode;
        string output;
        string error;
        // macOS permits one foreground ktrace owner. Concurrent failure reports must
        // release that resource before another owned tracer starts, including on cancellation.
        await s_fileActivityCaptureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (exitCode, output, error) = await DebuggerTestProcess.RunAsync(
                startInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_fileActivityCaptureGate.Release();
        }
        const int MaximumCharacters = 1024 * 1024;
        string report = $"Filesystem capture exit code: {exitCode}{Environment.NewLine}{error}{Environment.NewLine}{output}";
        if (report.Length > MaximumCharacters)
        {
            report = report[..MaximumCharacters] + Environment.NewLine + "Filesystem capture truncated.";
        }
        await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
        testContext.AddResultFile(path);
        testContext.WriteLine($"Owned process filesystem capture exited with {exitCode}: {path}.");
    }

    private static async Task CaptureManagedProcessAsync(
        int processId, string directory, TestContext testContext, CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, $"process-{processId}.nettrace");
        testContext.WriteLine($"Capturing managed stacks for process {processId} into {path}.");
        try
        {
            await DebuggerManagedStackCapture.CaptureAsync(processId, path, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Managed stack capture completed for process {processId}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or DiagnosticsClientException or ObjectDisposedException)
        {
            testContext.WriteLine($"Managed stack capture for {processId}: {exception.Message}");
        }
        finally
        {
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
    }

    private static async Task CaptureProcessAsync(
        int processId, string directory, TestContext testContext, CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, $"process-{processId}.sample.txt");
        bool hostedRunner = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(hostedRunner ? "/usr/bin/sudo" : "/usr/bin/sample");
        if (hostedRunner)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("/usr/bin/sample");
        }

        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-file");
        startInfo.ArgumentList.Add(path);
        testContext.WriteLine($"Sampling process {processId} into {path}.");
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo, cancellationToken,
            line => testContext.WriteLine($"sample {processId}: {line}")).ConfigureAwait(false);
        testContext.WriteLine($"Native stack capture for {processId} exited with {exitCode}: {output}{error}");
        if (File.Exists(path))
        {
            testContext.AddResultFile(path);
        }
    }
}
