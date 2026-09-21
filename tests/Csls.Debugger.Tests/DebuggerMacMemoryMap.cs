using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Retains bounded memory-region protections and sharing information for an owned macOS process.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class DebuggerMacMemoryMap
{
    private const int InvalidAddress = 1;
    private const int MaximumRegions = 4096;
    private const uint MaximumNestingDepth = 32;

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
        string path = Path.Join(directory, $"process-{processId}.regions.txt");
        try
        {
            string report = CaptureRegions(processId, cancellationToken);
            await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
            testContext.WriteLine($"Captured memory regions for {processId}: {path}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            testContext.WriteLine($"Memory regions for {processId}: {exception.Message}");
        }
        finally
        {
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
    }

    private static unsafe string CaptureRegions(int processId, CancellationToken cancellationToken)
    {
        uint currentTask = CurrentTask();
        int result = OpenTask(currentTask, processId, out uint targetTask);
        if (result != 0)
        {
            throw new InvalidOperationException($"Opening the owned process task failed with Mach error {result}.");
        }

        string report;
        int release;
        try
        {
            report = ReadRegions(targetTask, processId, cancellationToken);
        }
        finally
        {
            release = ReleasePort(currentTask, targetTask);
        }

        if (release != 0)
        {
            throw new InvalidOperationException($"Releasing the owned process task failed with Mach error {release}.");
        }

        return report;
    }

    private static unsafe string ReadRegions(uint targetTask, int processId, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine(FormattableString.Invariant($"Process memory regions for [{processId}]:"));
        report.AppendLine("ADDRESS RANGE                              BYTES CURRENT/MAX USER TAG SHARE");
        ulong address = 0;
        uint depth = 0;
        int visited = 0;
        int captured = 0;
        while (visited < MaximumRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = new DebuggerMacRegionInfo();
            uint infoCount = (uint)(sizeof(DebuggerMacRegionInfo) / sizeof(uint));
            ulong size;
            int result = QueryRegion(targetTask, ref address, out size, ref depth, &region, ref infoCount);
            if (result != 0)
            {
                if (result == InvalidAddress && captured != 0)
                {
                    break;
                }

                throw new InvalidOperationException($"Reading memory regions for {processId} failed with Mach error {result}.");
            }

            if (infoCount != (uint)(sizeof(DebuggerMacRegionInfo) / sizeof(uint)) || size == 0 ||
                ulong.MaxValue - address < size)
            {
                throw new InvalidDataException("The process region API returned an invalid address range.");
            }

            visited++;
            if (region._isSubmap != 0)
            {
                if (depth >= MaximumNestingDepth)
                {
                    throw new InvalidDataException("The process region map exceeded the nesting bound.");
                }

                depth++;
                continue;
            }

            ulong end = address + size;
            report.AppendLine(FormattableString.Invariant(
                $"0x{address:X16}-0x{end:X16} {size,12} {FormatProtection(region._protection)}/{FormatProtection(region._maxProtection)} {region._userTag,8} {FormatShareMode(region._shareMode)}"));
            address = end;
            captured++;
        }

        if (visited == MaximumRegions)
        {
            report.AppendLine(FormattableString.Invariant($"Report bounded to {MaximumRegions} regions."));
        }

        return report.ToString();
    }

    private static string FormatProtection(uint protection) => new([
        (protection & 1) != 0 ? 'r' : '-',
        (protection & 2) != 0 ? 'w' : '-',
        (protection & 4) != 0 ? 'x' : '-'
    ]);

    private static string FormatShareMode(byte shareMode) => shareMode switch
    {
        1 => "COW",
        2 => "PRIVATE",
        3 => "EMPTY",
        4 => "SHARED",
        5 => "TRUE_SHARED",
        6 => "PRIVATE_ALIASED",
        7 => "SHARED_ALIASED",
        8 => "LARGE_PAGE",
        _ => shareMode.ToString(CultureInfo.InvariantCulture)
    };

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_task_self")]
    private static partial uint CurrentTask();

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "task_for_pid")]
    private static partial int OpenTask(uint currentTask, int processId, out uint targetTask);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_vm_region_recurse")]
    private static unsafe partial int QueryRegion(uint targetTask, ref ulong address, out ulong size,
        ref uint depth, DebuggerMacRegionInfo* region, ref uint infoCount);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_port_deallocate")]
    private static partial int ReleasePort(uint currentTask, uint targetTask);
}
