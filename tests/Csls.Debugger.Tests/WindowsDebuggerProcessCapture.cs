using Microsoft.Diagnostics.NETCore.Client;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures native stacks from a failed Windows test's retained process tree with an independent deadline.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDebuggerProcessCapture
{
    /// <summary>
    /// Terminates a captured fixture and observes its retained kernel process handle before offline inspection.
    /// </summary>
    /// <param name="process">The caller-owned fixture whose collector is independently owned and reaped.</param>
    /// <returns>A task completed after the Windows process object is signaled.</returns>
    internal static async Task RetireTargetAsync(Process process)
    {
        if (!process.HasExited)
        {
            // The collector owns its PSS clones independently. A retired clone can remain enumerable while
            // another process holds a query handle; it is not an execution child owned by this fixture.
            process.Kill();
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        // The exit code can become observable before the kernel process object is signaled.
        bool signaled = await Task.Run(() => process.WaitForExit(TimeSpan.FromSeconds(10)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(signaled,
            "The captured target's Windows process handle must be signaled before offline inspection.");
    }

    /// <summary>
    /// Retains bounded native evidence before the test releases its adapter and target processes.
    /// </summary>
    /// <param name="hostProcessId">The still-owned adapter process identifier.</param>
    /// <param name="testContext">The test that owns the process tree and diagnostic artifacts.</param>
    /// <returns>The artifact directory after capture or the independent diagnostic deadline.</returns>
    internal static async Task<string> CaptureTreeAsync(int hostProcessId, TestContext testContext)
    {
        string directory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results",
            $"native-stacks-{hostProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var ownership = new DisposableCollection<Process>();
        var processes = new List<Process>();
        var diagnostics = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            RetainProcess(hostProcessId, processes, ownership);
            for (int index = 0; index < processes.Count && processes.Count < 8; index++)
            {
                WindowsProcessSnapshot.VisitChildren(processes[index].Id, processId =>
                {
                    if (processes.Count < 8 && !processes.Any(process => process.Id == processId))
                    {
                        RetainProcess(processId, processes, ownership);
                    }
                });
            }
            diagnostics.AppendLine(CultureInfo.InvariantCulture,
                $"{testContext.TestName}: owned processes {string.Join(", ", processes.Select(p => p.Id))}");
            string[] results = await Task.WhenAll(processes.Select(process =>
                CaptureOwnedProcessAsync(process, Path.Join(directory, $"process-{process.Id}.dmp"), cancellation.Token)))
                .ConfigureAwait(false);
            foreach (string result in results)
            {
                diagnostics.AppendLine(result);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            diagnostics.AppendLine(exception.ToString());
        }
        finally
        {
            string logPath = Path.Join(directory, "capture.log");
            await File.WriteAllTextAsync(logPath, diagnostics.ToString(), CancellationToken.None).ConfigureAwait(false);
            testContext.WriteLine(diagnostics.ToString());
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                testContext.AddResultFile(path);
            }
        }
        return directory;
    }

    /// <summary>
    /// Runs a separate collector against the exact process identity while its caller retains the process handle.
    /// </summary>
    /// <param name="process">The caller-owned process with a retained native identity.</param>
    /// <param name="path">The new dump path.</param>
    /// <param name="cancellationToken">Cancels and reaps the collector independently of the captured process.</param>
    /// <param name="captureType">An optional managed dump policy, with native thread capture as the default.</param>
    /// <returns>The collector exit status and diagnostics.</returns>
    internal static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(
        Process process, string path, CancellationToken cancellationToken, DumpType? captureType = null)
    {
        _ = process.SafeHandle;
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        startInfo.ArgumentList.Add(Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "bin",
            "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"));
        startInfo.ArgumentList.Add("--windows-native-dump");
        startInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(captureType switch
        {
            null => "threads",
            DumpType.Normal => "normal",
            DumpType.Triage => "triage",
            DumpType.WithHeap => "heap",
            DumpType.Full => "full",
            _ => throw new ArgumentOutOfRangeException(nameof(captureType))
        });
        (int collectorId, int exitCode, string output, string error) =
            await DebuggerTestProcess.RunWithIdentityAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            string crash = await DebuggerWindowsCrashDiagnostics.ReadAsync(collectorId).ConfigureAwait(false);
            error = $"Native dump collector {collectorId} exited with code {exitCode}.{Environment.NewLine}{error}{crash}";
        }
        return (exitCode, output, error);
    }

    private static void RetainProcess(int processId, List<Process> processes, DisposableCollection<Process> ownership)
    {
        Process process = ownership.Acquire(() => Process.GetProcessById(processId));
        _ = process.SafeHandle;
        processes.Add(process);
    }

    private static async Task<string> CaptureOwnedProcessAsync(Process process, string path, CancellationToken cancellationToken)
    {
        try
        {
            (int exitCode, string output, string error) = await CaptureAsync(process, path, cancellationToken)
                .ConfigureAwait(false);
            return $"Process {process.Id}, collector exit {exitCode}: {output}{error}";
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return $"Process {process.Id}: {exception}";
        }
    }
}
