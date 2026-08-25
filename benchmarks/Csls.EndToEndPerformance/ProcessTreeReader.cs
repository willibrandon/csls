using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Captures process-tree membership and memory without platform-specific command shells.
/// </summary>
internal static partial class ProcessTreeReader
{
    private const uint SnapshotProcesses = 0x00000002;

    /// <summary>
    /// Captures the complete descendant tree rooted at the supplied process.
    /// </summary>
    /// <param name="rootProcessId">The launcher process identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The process count and summed memory snapshot.</returns>
    internal static async Task<ProcessTreeSnapshot> CaptureAsync(
        int rootProcessId,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<int, int> parents = OperatingSystem.IsWindows()
            ? ReadWindowsParentMap()
            : OperatingSystem.IsLinux()
                ? ReadLinuxParentMap()
                : await ReadPsParentMapAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> processIds = CollectProcessTree(rootProcessId, parents);
        long workingSet = 0;
        long privateMemory = 0;
        long processorTimeTicks = 0;
        foreach (int processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                workingSet = checked(workingSet + process.WorkingSet64);
                privateMemory = checked(privateMemory + process.PrivateMemorySize64);
                processorTimeTicks = checked(
                    processorTimeTicks + process.TotalProcessorTime.Ticks);
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                Win32Exception)
            {
                continue;
            }
        }

        return new ProcessTreeSnapshot
        {
            ProcessIds = [.. processIds.Order()],
            ProcessCount = processIds.Count,
            WorkingSetBytes = workingSet,
            PrivateMemoryBytes = privateMemory,
            ProcessorTimeTicks = processorTimeTicks
        };
    }

    private static HashSet<int> CollectProcessTree(
        int rootProcessId,
        IReadOnlyDictionary<int, int> parents)
    {
        var result = new HashSet<int> { rootProcessId };
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out int parentProcessId))
        {
            foreach ((int processId, int observedParentProcessId) in parents)
            {
                if (observedParentProcessId == parentProcessId && result.Add(processId))
                {
                    pending.Enqueue(processId);
                }
            }
        }

        return result;
    }

    private static Dictionary<int, int> ReadLinuxParentMap()
    {
        var result = new Dictionary<int, int>();
        foreach (string processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            string name = Path.GetFileName(processDirectory);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
            {
                continue;
            }

            try
            {
                string processStat = File.ReadAllText(Path.Join(processDirectory, "stat"));
                int commandEnd = processStat.LastIndexOf(')');
                if (commandEnd < 0 || commandEnd + 2 >= processStat.Length)
                {
                    continue;
                }

                ReadOnlySpan<char> remaining = processStat.AsSpan(commandEnd + 2);
                int stateEnd = remaining.IndexOf(' ');
                if (stateEnd < 0)
                {
                    continue;
                }

                remaining = remaining[(stateEnd + 1)..];
                int parentEnd = remaining.IndexOf(' ');
                ReadOnlySpan<char> parentText = parentEnd < 0
                    ? remaining
                    : remaining[..parentEnd];
                if (int.TryParse(
                    parentText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parentProcessId))
                {
                    result[processId] = parentProcessId;
                }
            }
            catch (Exception exception) when (exception is
                DirectoryNotFoundException or
                FileNotFoundException or
                IOException or
                UnauthorizedAccessException)
            {
                continue;
            }
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, int> ReadWindowsParentMap()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var result = new Dictionary<int, int>();
        var entry = new WindowsProcessEntry
        {
            _size = checked((uint)Marshal.SizeOf<WindowsProcessEntry>())
        };
        if (!Process32First(snapshot, ref entry))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        do
        {
            result[checked((int)entry._processId)] = checked((int)entry._parentProcessId);
            entry._size = checked((uint)Marshal.SizeOf<WindowsProcessEntry>());
        }
        while (Process32Next(snapshot, ref entry));

        const int noMoreFiles = 18;
        int error = Marshal.GetLastPInvokeError();
        if (error != noMoreFiles)
        {
            throw new Win32Exception(error);
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<int, int>> ReadPsParentMapAsync(
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/ps",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The process table reader did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The process table reader exited with code {process.ExitCode}: {error.Trim()}");
        }

        var result = new Dictionary<int, int>();
        foreach (string line in output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 2 &&
                int.TryParse(
                    fields[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int processId) &&
                int.TryParse(
                    fields[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parentProcessId))
            {
                result[processId] = parentProcessId;
            }
        }

        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool Process32First(
        SafeFileHandle snapshot,
        ref WindowsProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool Process32Next(
        SafeFileHandle snapshot,
        ref WindowsProcessEntry entry);
}
