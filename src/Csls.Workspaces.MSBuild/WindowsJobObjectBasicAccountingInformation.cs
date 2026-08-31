using System.Runtime.InteropServices;

namespace Csls.Workspaces;

/// <summary>
/// Mirrors the native basic job-object accounting information layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsJobObjectBasicAccountingInformation
{
    /// <summary>
    /// Gets or sets the total user-mode execution time.
    /// </summary>
    internal long _totalUserTime;

    /// <summary>
    /// Gets or sets the total kernel-mode execution time.
    /// </summary>
    internal long _totalKernelTime;

    /// <summary>
    /// Gets or sets the user-mode execution time for completed processes.
    /// </summary>
    internal long _thisPeriodTotalUserTime;

    /// <summary>
    /// Gets or sets the kernel-mode execution time for completed processes.
    /// </summary>
    internal long _thisPeriodTotalKernelTime;

    /// <summary>
    /// Gets or sets the total number of page faults across assigned processes.
    /// </summary>
    internal uint _totalPageFaultCount;

    /// <summary>
    /// Gets or sets the total number of processes assigned to the job.
    /// </summary>
    internal uint _totalProcesses;

    /// <summary>
    /// Gets or sets the number of active processes assigned to the job.
    /// </summary>
    internal uint _activeProcesses;

    /// <summary>
    /// Gets or sets the number of processes terminated for exceeding a limit.
    /// </summary>
    internal uint _totalTerminatedProcesses;
}
