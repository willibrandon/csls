using System.ComponentModel;
using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Reads hosted macOS debugging authorization evidence after an owned target fails to start.
/// </summary>
internal static class DebuggerMacAuthorizationDiagnostics
{
    private const int MaximumOutputCharacters = 32768;

    /// <summary>
    /// Captures the runner identity, task-port authorization policy, and recent authorization messages.
    /// </summary>
    /// <param name="testContext">The failing test that retains the diagnostic output.</param>
    /// <param name="cancellationToken">The shared process-diagnostics collection deadline.</param>
    /// <returns>A task that completes when the read-only commands finish or collection is cancelled.</returns>
    internal static Task CaptureAsync(TestContext testContext, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS() ||
            !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(
            RunAsync("/usr/bin/id", [], testContext, cancellationToken),
            RunAsync("/usr/sbin/DevToolsSecurity", ["--status"], testContext, cancellationToken),
            Environment.ProcessPath is string hostPath
                ? RunAsync("/usr/bin/codesign", ["--display", "--verbose=4", "--entitlements", ":-", hostPath],
                    testContext, cancellationToken)
                : Task.CompletedTask,
            RunAsync("/usr/bin/security", ["authorizationdb", "read", "system.privilege.taskport"],
                testContext, cancellationToken),
            RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/log", "show", "--last", "2m", "--style", "compact",
                "--info", "--predicate", "process == 'taskgated' OR process == 'taskgated-helper' OR process == 'authd'"],
                testContext, cancellationToken));
    }

    private static async Task RunAsync(
        string command, string[] arguments, TestContext testContext, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Debug authorization: {command} {string.Join(' ', arguments)} (exit {exitCode}).");
            testContext.WriteLine(output[^Math.Min(output.Length, MaximumOutputCharacters)..]);
            testContext.WriteLine(error[^Math.Min(error.Length, MaximumOutputCharacters)..]);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            testContext.WriteLine($"Debug authorization: {command}: {exception.Message}");
        }
    }
}
