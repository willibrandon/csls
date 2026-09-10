using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures compact native macOS core files from test-owned managed targets.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class DebuggerMacCoreCapture
{
    /// <summary>
    /// Signs an owned collector copy and captures modified target pages with native thread identities and file references.
    /// </summary>
    /// <param name="processId">The independently running test-owned managed process.</param>
    /// <param name="path">The new core file inside the fixture's owned directory.</param>
    /// <param name="progress">Receives the collector's actual diagnostic output.</param>
    /// <param name="diagnosticContext">Retains native collector stacks if capture is canceled.</param>
    /// <param name="cancellationToken">Cancels signing and collection and reaps the collector process.</param>
    internal static async Task CaptureAsync(int processId, string path, Action<string> progress, TestContext? diagnosticContext,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The native core requires an owned directory.");
        string collector = Path.Join(directory, "gcore");
        string entitlements = Path.Join(directory, "collector-entitlements.plist");
        File.Copy("/usr/bin/gcore", collector, overwrite: false);
        try
        {
            await File.WriteAllTextAsync(entitlements,
                "<plist version=\"1.0\"><dict><key>com.apple.security.cs.debugger</key><true/></dict></plist>",
                cancellationToken).ConfigureAwait(false);
            await RunAsync("/usr/bin/codesign",
                ["--force", "--sign", "-", "--entitlements", entitlements, collector], progress, diagnosticContext, cancellationToken)
                .ConfigureAwait(false);
            // Cache the captured pages for immediate offline inspection. The collector writes chunks smaller than 2 GiB.
            await RunAsync(collector,
                ["-s", "-x", "compact", "-t", "2097152", "-v", "-o", path, processId.ToString(CultureInfo.InvariantCulture)],
                progress, diagnosticContext, cancellationToken,
                (process, report, token) => DebuggerCaptureResourceObservation.ObserveAsync(process, path, report, token))
                .ConfigureAwait(false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                progress($"Capture file {Path.GetFileName(file)}: {new FileInfo(file).Length} bytes.");
            }
            throw;
        }
        finally
        {
            File.Delete(collector);
            File.Delete(entitlements);
        }
    }

    private static async Task RunAsync(string executable, string[] arguments, Action<string> progress,
        TestContext? diagnosticContext, CancellationToken cancellationToken,
        Func<Process, Action<string>, CancellationToken, Task>? observeProcess = null)
    {
        var start = new ProcessStartInfo(executable);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            start, cancellationToken, progress, diagnosticContext, observeProcess)
            .ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, $"Native core collector failed: {output}{Environment.NewLine}{error}");
    }
}
