using System.Runtime.InteropServices;

namespace Csls.Workspaces;

/// <summary>
/// Mirrors the native extended job-object limit information layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsJobObjectExtendedLimitInformation
{
    /// <summary>
    /// Gets or sets the basic native job-object limits.
    /// </summary>
    internal WindowsJobObjectBasicLimitInformation _basicLimitInformation;

    /// <summary>
    /// Gets or sets the total read-operation count.
    /// </summary>
    internal ulong _readOperationCount;

    /// <summary>
    /// Gets or sets the total write-operation count.
    /// </summary>
    internal ulong _writeOperationCount;

    /// <summary>
    /// Gets or sets the total other-operation count.
    /// </summary>
    internal ulong _otherOperationCount;

    /// <summary>
    /// Gets or sets the total bytes read.
    /// </summary>
    internal ulong _readTransferCount;

    /// <summary>
    /// Gets or sets the total bytes written.
    /// </summary>
    internal ulong _writeTransferCount;

    /// <summary>
    /// Gets or sets the total bytes transferred by other operations.
    /// </summary>
    internal ulong _otherTransferCount;

    /// <summary>
    /// Gets or sets the per-process memory limit.
    /// </summary>
    internal nuint _processMemoryLimit;

    /// <summary>
    /// Gets or sets the per-job memory limit.
    /// </summary>
    internal nuint _jobMemoryLimit;

    /// <summary>
    /// Gets or sets the peak per-process memory use.
    /// </summary>
    internal nuint _peakProcessMemoryUsed;

    /// <summary>
    /// Gets or sets the peak per-job memory use.
    /// </summary>
    internal nuint _peakJobMemoryUsed;
}
