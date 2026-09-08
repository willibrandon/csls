using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Retains Windows crash records for a failing test-owned target without changing machine crash settings.
/// </summary>
internal static class DebuggerWindowsCrashDiagnostics
{
    /// <summary>
    /// Reads recent Application Error events belonging to the target process on a hosted Windows test runner.
    /// </summary>
    /// <param name="processId">The operating-system identifier of the test-owned target.</param>
    /// <param name="testContext">The failing test that receives the diagnostic output.</param>
    /// <returns>A task that completes after the bounded event-log query.</returns>
    internal static async Task CaptureAsync(int processId, TestContext testContext)
    {
        string diagnostics = await ReadAsync(processId).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(diagnostics))
        {
            testContext.WriteLine(diagnostics);
        }
    }

    /// <summary>
    /// Reads the hosted Windows crash records for one explicitly identified test-owned process.
    /// </summary>
    /// <param name="processId">The operating-system identifier of the failed process.</param>
    /// <returns>The bounded query's output or its diagnostic failure.</returns>
    internal static async Task<string> ReadAsync(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 ||
            !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Event 1000 records the faulting application's PID as a hexadecimal EventData value.
        string hexadecimalId = $"0x{processId.ToString("x", CultureInfo.InvariantCulture)}";
        string decimalId = processId.ToString(CultureInfo.InvariantCulture);
        var startInfo = new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "wevtutil.exe"));
        startInfo.ArgumentList.Add("qe");
        startInfo.ArgumentList.Add("Application");
        startInfo.ArgumentList.Add("/q:*[System[Provider[@Name='Application Error'] and EventID=1000 and " +
            "TimeCreated[timediff(@SystemTime) <= 120000]] and EventData[Data[@Name='ProcessId']='" +
            hexadecimalId + "' or Data[@Name='ProcessId']='" + decimalId + "']]");
        startInfo.ArgumentList.Add("/rd:true");
        startInfo.ArgumentList.Add("/c:4");
        startInfo.ArgumentList.Add("/f:xml");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(startInfo, deadline.Token)
                .ConfigureAwait(false);
            return $"Windows crash events for target {decimalId}: exit {exitCode}.{Environment.NewLine}{output}{error}";
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return $"Windows crash event collection for target {decimalId}: {exception.Message}";
        }
    }
}
