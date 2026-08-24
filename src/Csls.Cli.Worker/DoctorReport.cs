using Csls.Control.Contracts;

namespace Csls.Cli.Worker;

/// <summary>
/// Describes a real SDK, language-server, workspace, and optional build inspection.
/// </summary>
internal sealed class DoctorReport
{
    /// <summary>
    /// Gets the absolute inspected workspace path.
    /// </summary>
    public required string WorkspacePath { get; init; }

    /// <summary>
    /// Gets the selected dotnet host path or command name.
    /// </summary>
    public required string DotNetHost { get; init; }

    /// <summary>
    /// Gets the SDK version selected for the workspace when resolution succeeded.
    /// </summary>
    public string? SdkVersion { get; init; }

    /// <summary>
    /// Gets the current operating-system description.
    /// </summary>
    public required string OperatingSystem { get; init; }

    /// <summary>
    /// Gets the current process architecture.
    /// </summary>
    public required string ProcessArchitecture { get; init; }

    /// <summary>
    /// Gets the requested absolute MSBuild binary-log path when one was supplied.
    /// </summary>
    public string? BinlogPath { get; init; }

    /// <summary>
    /// Gets the workspace folders loaded by the real language-server process.
    /// </summary>
    public required IReadOnlyList<ControlWorkspaceInfo> Workspaces { get; init; }

    /// <summary>
    /// Gets the Roslyn projects loaded by the real language-server process.
    /// </summary>
    public required IReadOnlyList<DoctorProject> Projects { get; init; }

    /// <summary>
    /// Gets the total source documents loaded by the real language-server process.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Gets the bounded compiler and analyzer diagnostics from the real workspace.
    /// </summary>
    public required IReadOnlyList<ControlDiagnosticInfo> Diagnostics { get; init; }

    /// <summary>
    /// Gets the actionable error and warning count included in this report.
    /// </summary>
    public int TotalDiagnostics { get; init; }

    /// <summary>
    /// Gets whether the diagnostic response omitted additional entries.
    /// </summary>
    public bool DiagnosticsTruncated { get; init; }

    /// <summary>
    /// Gets the Roslyn build hosts observed by the real language-server process.
    /// </summary>
    public required IReadOnlyList<ControlBuildHostInfo> BuildHosts { get; init; }

    /// <summary>
    /// Gets the bounded structured logs captured during workspace startup.
    /// </summary>
    public required IReadOnlyList<ControlLogEntry> Logs { get; init; }

    /// <summary>
    /// Gets every ordered doctor check.
    /// </summary>
    public required IReadOnlyList<DoctorCheck> Checks { get; init; }

    /// <summary>
    /// Gets whether every required capability passed.
    /// </summary>
    public bool IsHealthy => Checks.All(static check => check.Status != DoctorCheckStatus.Fail);
}
