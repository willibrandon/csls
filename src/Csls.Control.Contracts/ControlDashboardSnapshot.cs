namespace Csls.Control.Contracts;

/// <summary>
/// Describes one bounded dashboard observation of a live csls session.
/// </summary>
public sealed class ControlDashboardSnapshot
{
    /// <summary>
    /// Gets the live session identity and lifecycle state.
    /// </summary>
    public required ControlSessionInfo Session { get; init; }

    /// <summary>
    /// Gets the loaded workspace folders.
    /// </summary>
    public required IReadOnlyList<ControlWorkspaceInfo> Workspaces { get; init; }

    /// <summary>
    /// Gets the loaded Roslyn projects.
    /// </summary>
    public required IReadOnlyList<ControlProjectInfo> Projects { get; init; }

    /// <summary>
    /// Gets the loaded source documents.
    /// </summary>
    public required IReadOnlyList<ControlDocumentInfo> Documents { get; init; }

    /// <summary>
    /// Gets the bounded current compiler and analyzer diagnostics.
    /// </summary>
    public required IReadOnlyList<ControlDiagnosticInfo> Diagnostics { get; init; }

    /// <summary>
    /// Gets whether diagnostics were evaluated for this snapshot.
    /// </summary>
    public bool DiagnosticsLoaded { get; init; }

    /// <summary>
    /// Gets the total diagnostic count before result bounding.
    /// </summary>
    public int TotalDiagnostics { get; init; }

    /// <summary>
    /// Gets whether additional diagnostics were omitted from this snapshot.
    /// </summary>
    public bool DiagnosticsTruncated { get; init; }

    /// <summary>
    /// Gets the live request scheduler observation.
    /// </summary>
    public required ControlRequestSchedulerInfo Requests { get; init; }

    /// <summary>
    /// Gets the active Roslyn build-host observations.
    /// </summary>
    public required IReadOnlyList<ControlBuildHostInfo> BuildHosts { get; init; }

    /// <summary>
    /// Gets the current bounded cache observations.
    /// </summary>
    public required IReadOnlyList<ControlCacheInfo> Caches { get; init; }

    /// <summary>
    /// Gets the bounded recent structured worker logs.
    /// </summary>
    public required IReadOnlyList<ControlLogEntry> Logs { get; init; }
}
