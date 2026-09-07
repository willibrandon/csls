using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Reports native owners of an exact test fixture file after an access failure.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DebuggerFileLockDiagnostics
{
    /// <summary>
    /// Queries current native file owners without changing their execution state.
    /// </summary>
    /// <param name="path">The absolute file path whose owners are requested.</param>
    /// <returns>The process identifiers returned by the operating system.</returns>
    internal static int[] FindOwners(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("File-owner inspection requires an absolute path without null characters.", nameof(path));
        }

        using var session = DebuggerRestartManagerSession.Open();
        return session.FindOwners(path);
    }

    /// <summary>
    /// Records file-owner identities while preserving the test's original failure.
    /// </summary>
    /// <param name="path">The exact fixture file reported by the failed operation.</param>
    /// <param name="testContext">The diagnostic destination for the failing test.</param>
    internal static void Capture(string path, TestContext testContext)
    {
        try
        {
            int[] owners = FindOwners(path);
            testContext.WriteLine($"Native file owners for {path}: {string.Join(", ", owners)}.");
            foreach (int identifier in owners)
            {
                try
                {
                    using var process = Process.GetProcessById(identifier);
                    testContext.WriteLine($"File owner {identifier}: {process.ProcessName}.");
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    testContext.WriteLine($"File owner {identifier}: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            testContext.WriteLine($"Native file-owner inspection: {exception.Message}");
        }
    }
}
