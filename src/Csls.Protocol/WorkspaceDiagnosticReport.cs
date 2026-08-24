namespace Csls.Protocol;

/// <summary>
/// Returns bounded document diagnostic results for one loaded workspace snapshot.
/// </summary>
public sealed record WorkspaceDiagnosticReport
{
    /// <summary>
    /// Gets the ordered complete and unchanged document reports.
    /// </summary>
    public IReadOnlyList<WorkspaceDocumentDiagnosticReport> Items { get; init; } = [];
}
