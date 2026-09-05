using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures bounded native stacks from a failed test's macOS debugger process tree.
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
            var startInfo = new ProcessStartInfo("/bin/ps");
            startInfo.ArgumentList.Add("-axo");
            startInfo.ArgumentList.Add("pid=,ppid=");
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, cancellation.Token).ConfigureAwait(false);
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
            await Task.WhenAll(processes.Select(processId => CaptureProcessAsync(
                processId, directory, testContext, cancellation.Token))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            testContext.WriteLine($"Native stack capture: {exception.Message}");
        }
    }

    private static async Task CaptureProcessAsync(
        int processId, string directory, TestContext testContext, CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, $"process-{processId}.sample.txt");
        var startInfo = new ProcessStartInfo("/usr/bin/sample");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-file");
        startInfo.ArgumentList.Add(path);
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo, cancellationToken).ConfigureAwait(false);
        testContext.WriteLine($"Native stack capture for {processId} exited with {exitCode}: {output}{error}");
        if (File.Exists(path))
        {
            testContext.AddResultFile(path);
        }
    }
}
