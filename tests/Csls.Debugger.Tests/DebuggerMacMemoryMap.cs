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
    /// Captures the process memory map within the existing diagnostic deadline.
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
        bool hostedRunner = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(hostedRunner ? "/usr/bin/sudo" : "/usr/bin/vmmap");
        if (hostedRunner)
        {
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add("/usr/bin/vmmap");
        }
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add("-pages");
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(start, cancellationToken)
                .ConfigureAwait(false);
            string report = $"Memory map exit code: {exitCode}{Environment.NewLine}{error}{Environment.NewLine}{output}";
            const int MaximumCharacters = 1024 * 1024;
            if (report.Length > MaximumCharacters)
            {
                report = report[..MaximumCharacters] + Environment.NewLine + "Memory map truncated.";
            }
            await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Memory map for {processId} exited with {exitCode}: {path}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            testContext.WriteLine($"Memory map for {processId}: {exception.Message}");
        }
        finally
        {
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
    }
}
