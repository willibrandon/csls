using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures native stacks and kernel wait information from an independently owned Linux debugger worker.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxDebuggerProcessCapture
{
    private const int MaximumThreads = 256;
    private const int MaximumFileCharacters = 16 * 1024;
    private const int MaximumReportCharacters = 1024 * 1024;

    /// <summary>
    /// Records the worker's pre-capture wait state and requests native thread contexts through its diagnostics socket.
    /// </summary>
    /// <param name="worker">The observed worker retained by the caller until capture finishes.</param>
    /// <param name="directory">The caller-owned empty artifact directory.</param>
    /// <param name="cancellationToken">Cancels kernel observation or the diagnostic request.</param>
    /// <returns>The absolute path of the completed native dump.</returns>
    internal static async Task<string> CaptureAsync(Process worker, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        cancellationToken.ThrowIfCancellationRequested();
        if (worker.HasExited)
        {
            throw new InvalidOperationException("The owned debugger worker exited before capture.");
        }

        int processId = worker.Id;
        string root = Path.Join("/proc", processId.ToString(CultureInfo.InvariantCulture));
        var report = new StringBuilder();
        bool reportComplete = false;
        await AppendAsync("status").ConfigureAwait(false);
        await AppendAsync("stat").ConfigureAwait(false);
        await AppendAsync("maps").ConfigureAwait(false);
        int threads = 0;
        foreach (string task in Directory.EnumerateDirectories(Path.Join(root, "task")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reportComplete)
            {
                break;
            }
            if (threads++ == MaximumThreads)
            {
                report.AppendLine("Kernel wait report reached its capture limit.");
                break;
            }
            string thread = Path.GetFileName(task);
            await AppendAsync(Path.Join("task", thread, "status")).ConfigureAwait(false);
            await AppendAsync(Path.Join("task", thread, "wchan")).ConfigureAwait(false);
            await AppendAsync(Path.Join("task", thread, "syscall")).ConfigureAwait(false);
            await AppendAsync(Path.Join("task", thread, "stack")).ConfigureAwait(false);
        }
        string prefix = Path.Join(directory, $"process-{processId}");
        await File.WriteAllTextAsync(prefix + ".proc.txt", report.ToString(), cancellationToken).ConfigureAwait(false);
        string dump = prefix + ".dmp";
        await new DiagnosticsClient(processId).WriteDumpAsync(DumpType.Normal, dump,
            logDumpGeneration: false, cancellationToken).ConfigureAwait(false);
        return dump;

        async Task AppendAsync(string relativePath)
        {
            if (reportComplete)
            {
                return;
            }
            if (report.Length + relativePath.Length + 1024 >= MaximumReportCharacters)
            {
                report.AppendLine("Kernel wait report reached its capture limit.");
                reportComplete = true;
                return;
            }
            report.AppendLine(relativePath);
            try
            {
                using var reader = new StreamReader(Path.Join(root, relativePath));
                char[] contents = new char[Math.Min(MaximumFileCharacters,
                    MaximumReportCharacters - report.Length - 1024)];
                int length = await reader.ReadBlockAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                report.Append(contents, 0, length).AppendLine();
                if (length == contents.Length)
                {
                    report.AppendLine("Kernel file reached its capture limit.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report.Append(exception.Message.AsSpan(0, Math.Min(exception.Message.Length, 512))).AppendLine();
            }
        }
    }
}
