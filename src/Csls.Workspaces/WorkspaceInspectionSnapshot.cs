namespace Csls.Workspaces;

/// <summary>
/// Describes a bounded, immutable inspection of the current Roslyn workspace generation.
/// </summary>
public sealed class WorkspaceInspectionSnapshot
{
    /// <summary>
    /// Gets the inspected workspace generation.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// Gets the loaded workspace folders.
    /// </summary>
    public required IReadOnlyList<WorkspaceFolderInspection> Workspaces { get; init; }

    /// <summary>
    /// Gets the loaded Roslyn projects.
    /// </summary>
    public required IReadOnlyList<WorkspaceProjectInspection> Projects { get; init; }

    /// <summary>
    /// Gets the loaded source documents.
    /// </summary>
    public required IReadOnlyList<WorkspaceDocumentInspection> Documents { get; init; }

    /// <summary>
    /// Gets the bounded current compiler and analyzer diagnostics.
    /// </summary>
    public required IReadOnlyList<WorkspaceDiagnosticInspection> Diagnostics { get; init; }

    /// <summary>
    /// Gets whether diagnostics were evaluated for this inspection.
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
    /// Gets the active Roslyn build-host observations.
    /// </summary>
    public required IReadOnlyList<WorkspaceBuildHostInspection> BuildHosts { get; init; }

    /// <summary>
    /// Gets the number of cached project diagnostic computations.
    /// </summary>
    public int DiagnosticCacheEntries { get; init; }
}
