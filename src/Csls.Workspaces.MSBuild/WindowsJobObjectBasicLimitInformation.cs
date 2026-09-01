using System.Runtime.InteropServices;

namespace Csls.Workspaces;

/// <summary>
/// Mirrors the native basic job-object limit information layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsJobObjectBasicLimitInformation
{
    /// <summary>
    /// Gets or sets the per-process user-mode execution limit.
    /// </summary>
    internal long _perProcessUserTimeLimit;

    /// <summary>
    /// Gets or sets the per-job user-mode execution limit.
    /// </summary>
    internal long _perJobUserTimeLimit;

    /// <summary>
    /// Gets or sets the enabled native job-object limit flags.
    /// </summary>
    internal uint _limitFlags;

    /// <summary>
    /// Gets or sets the minimum process working-set size.
    /// </summary>
    internal nuint _minimumWorkingSetSize;

    /// <summary>
    /// Gets or sets the maximum process working-set size.
    /// </summary>
    internal nuint _maximumWorkingSetSize;

    /// <summary>
    /// Gets or sets the maximum number of active processes.
    /// </summary>
    internal uint _activeProcessLimit;

    /// <summary>
    /// Gets or sets the processor affinity mask.
    /// </summary>
    internal nuint _affinity;

    /// <summary>
    /// Gets or sets the process priority class.
    /// </summary>
    internal uint _priorityClass;

    /// <summary>
    /// Gets or sets the process scheduling class.
    /// </summary>
    internal uint _schedulingClass;
}
