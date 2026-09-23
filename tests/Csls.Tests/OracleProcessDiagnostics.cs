using Csls.Debugger.Tests;
using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Tests;

/// <summary>
/// Retains the live Roslyn oracle and BuildHost state when workspace loading stalls.
/// </summary>
internal static class OracleProcessDiagnostics
{
    private const int MaximumProcesses = 8;

    /// <summary>
    /// Captures bounded diagnostics from the test-owned Linux oracle process tree.
    /// </summary>
    /// <param name="oracleProcessId">The language-server process owned by the parity test.</param>
    /// <param name="directory">The parity test's artifact directory.</param>
    /// <param name="testContext">The test context receiving completed diagnostic artifacts.</param>
    /// <returns>Completion after the process tree is inspected or the diagnostic deadline expires.</returns>
    internal static async Task CaptureAsync(int oracleProcessId, string directory, TestContext testContext)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        List<int> processes = FindOwnedProcesses(oracleProcessId, testContext);
        testContext.WriteLine($"Oracle process tree at cancellation: {string.Join(", ", processes)}.");
        foreach (int processId in processes.AsEnumerable().Reverse())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                string dump = await LinuxDebuggerProcessCapture.CaptureAsync(
                    process, directory, cancellation.Token).ConfigureAwait(false);
                testContext.AddResultFile(dump);
                testContext.AddResultFile(Path.Join(directory, $"process-{processId}.proc.txt"));
                testContext.WriteLine($"Captured oracle process {processId}: {dump}.");
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException or
                Win32Exception or DiagnosticsClientException)
            {
                testContext.WriteLine($"Oracle process {processId} diagnostic capture: {exception.Message}");
            }
        }
    }

    private static List<int> FindOwnedProcesses(int rootProcessId, TestContext testContext)
    {
        var processes = new List<int> { rootProcessId };
        var seen = new HashSet<int> { rootProcessId };
        for (int index = 0; index < processes.Count && processes.Count < MaximumProcesses; index++)
        {
            string tasks = Path.Join("/proc", processes[index].ToString(CultureInfo.InvariantCulture), "task");
            try
            {
                int[] children =
                [
                    .. Directory.EnumerateDirectories(tasks)
                        .Select(static task => Path.Join(task, "children"))
                        .Select(File.ReadAllText)
                        .SelectMany(static text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Select(static child => int.TryParse(child, NumberStyles.None,
                            CultureInfo.InvariantCulture, out int id) ? id : 0)
                        .Where(static id => id > 0)
                        .Distinct()
                        .Except(seen)
                        .Take(MaximumProcesses - processes.Count)
                ];
                processes.AddRange(children);
                seen.UnionWith(children);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                testContext.WriteLine($"Oracle process tree inspection: {exception.Message}");
            }
        }

        return processes;
    }
}
