using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures native stacks and kernel wait information from independently owned Linux debugger processes.
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
        _ = await CaptureKernelStateAsync(worker, directory, cancellationToken).ConfigureAwait(false);
        string dump = Path.Join(directory, $"process-{worker.Id}.dmp");
        await new DiagnosticsClient(worker.Id).WriteDumpAsync(DumpType.Normal, dump,
            logDumpGeneration: false, cancellationToken).ConfigureAwait(false);
        return dump;
    }

    /// <summary>
    /// Reads bounded kernel wait information while preserving the process's existing debugger attachment.
    /// </summary>
    /// <param name="process">The owned process retained by the caller until observation finishes.</param>
    /// <param name="directory">The caller-owned artifact directory.</param>
    /// <param name="cancellationToken">Cancels kernel observation and report writing.</param>
    /// <returns>The absolute path of the completed kernel report.</returns>
    internal static async Task<string> CaptureKernelStateAsync(Process process, string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited)
        {
            throw new InvalidOperationException("The owned process exited before capture.");
        }

        int processId = process.Id;
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
        string path = Path.Join(directory, $"process-{processId}.proc.txt");
        await File.WriteAllTextAsync(path, report.ToString(), cancellationToken).ConfigureAwait(false);
        return path;

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
