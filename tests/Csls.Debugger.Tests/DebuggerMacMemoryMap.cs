using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Retains memory-region protections and backing information for a failed test's owned macOS process.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class DebuggerMacMemoryMap
{
    /// <summary>
    /// Captures process memory regions within the existing diagnostic deadline.
    /// </summary>
    /// <param name="processId">The process whose ownership was established by the diagnostic caller.</param>
    /// <param name="directory">The diagnostic directory that retains the report.</param>
    /// <param name="testContext">The test context attaching diagnostic evidence.</param>
    /// <param name="cancellationToken">Bounds the read-only observation.</param>
    /// <returns>Completion after the report is retained or its diagnostic failure is reported.</returns>
    internal static async Task CaptureAsync(int processId, string directory, TestContext testContext,
        CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, $"process-{processId}.vmmap.txt");
        // The macOS task-port authorization rule requires the observer and target to share a user.
        var start = new ProcessStartInfo("/usr/bin/vmmap");
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add("-pages");
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        long started = Stopwatch.GetTimestamp();
        int observedLines = 0;
        testContext.WriteLine($"Starting memory map for {processId} as {Environment.UserName}.");
        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(start, cancellationToken,
                line =>
                {
                    if (Interlocked.Increment(ref observedLines) <= 4)
                    {
                        testContext.WriteLine($"Memory map {processId} output: {line[..Math.Min(line.Length, 256)]}");
                    }
                }, observeProcess: (process, _, token) => ObserveCaptureAsync(process, testContext, token))
                .ConfigureAwait(false);
            string report = $"Memory map exit code: {exitCode}{Environment.NewLine}{error}{Environment.NewLine}{output}";
            const int MaximumCharacters = 1024 * 1024;
            if (report.Length > MaximumCharacters)
            {
                report = report[..MaximumCharacters] + Environment.NewLine + "Memory map truncated.";
            }
            await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Memory map for {processId} exited with {exitCode} after " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: {path}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            testContext.WriteLine($"Memory map for {processId} after " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms " +
                $"and {Volatile.Read(ref observedLines)} output lines: {exception.Message}");
        }
        finally
        {
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
    }

    private static async Task ObserveCaptureAsync(Process process, TestContext testContext,
        CancellationToken cancellationToken)
    {
        Task exit = process.WaitForExitAsync(cancellationToken);
        if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(4), cancellationToken))
            .ConfigureAwait(false) == exit)
        {
            await exit.ConfigureAwait(false);
            return;
        }

        if (!process.HasExited)
        {
            var start = new ProcessStartInfo("/bin/ps");
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("pid,ppid,state,wchan,pcpu,time,etime,rss,vsz,comm");
            (int code, string output, string error) = await DebuggerTestProcess.RunAsync(start, cancellationToken)
                .ConfigureAwait(false);
            testContext.WriteLine($"Memory map child {process.Id} after four seconds (exit {code}): " +
                $"{output}{error}");
        }

        await exit.ConfigureAwait(false);
    }
}
