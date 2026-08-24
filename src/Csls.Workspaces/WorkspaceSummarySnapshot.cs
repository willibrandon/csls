namespace Csls.Workspaces;

/// <summary>
/// Describes the current workspace generation and its loaded folder summaries.
/// </summary>
public sealed class WorkspaceSummarySnapshot
{
    /// <summary>
    /// Gets the inspected immutable workspace generation.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// Gets the loaded workspace folder summaries.
    /// </summary>
    public required IReadOnlyList<WorkspaceFolderInspection> Workspaces { get; init; }
}
