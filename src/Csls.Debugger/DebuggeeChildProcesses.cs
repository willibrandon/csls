using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Discovers and terminates direct child roots while their managed parent is stopped.
/// </summary>
internal static partial class DebuggeeChildProcesses
{
    /// <summary>
    /// Lists the positive direct child identifiers of the selected process from the operating system.
    /// </summary>
    internal static int[] GetIds(int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        if (OperatingSystem.IsWindows())
        {
            return WindowsProcessSnapshot.GetChildren(parentProcessId);
        }
        if (OperatingSystem.IsMacOS())
        {
            return GetMacChildren(parentProcessId);
        }
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxChildren(parentProcessId);
        }
        throw new PlatformNotSupportedException("Child process discovery requires Windows, Linux, or macOS.");
    }

    /// <summary>
    /// Terminates each live child subtree before its parent releases the runtime debugger connection.
    /// </summary>
    internal static void Terminate(int parentProcessId)
    {
        foreach (int processId in GetIds(parentProcessId))
        {
            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                continue;
            }
            using (process)
            {
                // Revalidate membership after acquiring the process, since a child can exit during discovery.
                if (!GetIds(parentProcessId).Contains(processId))
                {
                    continue;
                }
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    Debug.Assert(process.HasExited);
                }
            }
        }
    }

    private static int[] GetLinuxChildren(int parentProcessId)
    {
        var children = new List<int>();
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None,
                CultureInfo.InvariantCulture, out int processId) || processId <= 0)
            {
                continue;
            }
            try
            {
                using var reader = new StreamReader(Path.Join(directory, "stat"));
                if (ReadLinuxParent(reader, processId) == parentProcessId)
                {
                    children.Add(processId);
                }
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
        }
        return [.. children.Order()];
    }

    /// <summary>
    /// Reads a parent identifier from an owned procfs reader, accounting for exit after the file was opened.
    /// </summary>
    /// <param name="reader">The caller-owned reader for a Linux process stat file.</param>
    /// <param name="processId">The process identifier used in malformed-record diagnostics.</param>
    /// <returns>The parent identifier, or null when the kernel has retired the process.</returns>
    internal static int? ReadLinuxParent(StreamReader reader, int processId)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string stat;
        try
        {
            stat = reader.ReadToEnd();
        }
        catch (IOException exception) when (exception.HResult == 3)
        {
            // Linux procfs returns ESRCH after its inode's task exits; .NET preserves the raw errno.
            return null;
        }
        int nameEnd = stat.LastIndexOf(')');
        ReadOnlySpan<char> fields = stat.AsSpan(nameEnd + 1).TrimStart();
        int stateEnd = fields.IndexOf(' ');
        if (nameEnd < 0 || stateEnd < 0)
        {
            throw new IOException($"The operating system returned an invalid process record for {processId}.");
        }
        fields = fields[(stateEnd + 1)..].TrimStart();
        int parentEnd = fields.IndexOf(' ');
        if (parentEnd < 0 || !int.TryParse(fields[..parentEnd], NumberStyles.None,
            CultureInfo.InvariantCulture, out int parent))
        {
            throw new IOException($"The operating system returned an invalid parent for {processId}.");
        }
        return parent;
    }

    [SupportedOSPlatform("macos")]
    private static unsafe int[] GetMacChildren(int parentProcessId)
    {
        const int MaximumChildren = 131072;
        for (int capacity = 16; capacity <= MaximumChildren; capacity *= 2)
        {
            int[] children = new int[capacity];
            int count;
            fixed (int* buffer = children)
            {
                count = ListMacChildProcesses(parentProcessId, buffer, checked(capacity * sizeof(int)));
            }
            if (count < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if (count < capacity)
            {
                return [.. children.Take(count).Where(static id => id > 0).Distinct().Order()];
            }
        }
        throw new InvalidOperationException($"Child process discovery exceeds {MaximumChildren} identifiers.");
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static unsafe partial int ListMacChildProcesses(int parentProcessId, int* buffer, int bufferBytes);
}
