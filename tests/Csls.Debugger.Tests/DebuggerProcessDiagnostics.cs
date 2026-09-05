using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures bounded native and managed stacks from a failed test's macOS debugger process tree.
/// </summary>
internal static class DebuggerProcessDiagnostics
{
    private const int MaximumProcesses = 8;

    /// <summary>
    /// Records native wait locations before a failed test releases its owned adapter and target.
    /// </summary>
    /// <param name="hostProcessId">The adapter launcher process owned by the test.</param>
    /// <param name="testContext">The test context that retains diagnostic artifact paths.</param>
    /// <returns>A task that completes after diagnostic collection or its bounded deadline.</returns>
    internal static async Task CaptureAsync(int hostProcessId, TestContext testContext)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            testContext.WriteLine($"Inspecting descendants of adapter process {hostProcessId}.");
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
                testContext.WriteLine($"Adapter process {hostProcessId} exited before native stack capture.");
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
            await Task.WhenAll(processes.SelectMany(processId => new[]
            {
                CaptureProcessAsync(processId, directory, testContext, cancellation.Token),
                CaptureManagedProcessAsync(processId, directory, testContext, cancellation.Token)
            }).Append(DebuggerMacAuthorizationDiagnostics.CaptureAsync(testContext, cancellation.Token)))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            testContext.WriteLine($"Native stack capture: {exception.Message}");
        }
    }

    private static async Task CaptureWaitStatesAsync(
        List<int> processes, TestContext testContext, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("/bin/ps");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(string.Join(",", processes));
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("pid,ppid,state,wchan,comm");
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo, cancellationToken).ConfigureAwait(false);
        testContext.WriteLine($"Owned process wait states (exit {exitCode}): {output}{error}");
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
