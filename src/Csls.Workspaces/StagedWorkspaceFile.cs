using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Holds one fully validated file mutation until the workspace edit commits atomically.
/// </summary>
internal sealed record StagedWorkspaceFile
{
    /// <summary>
    /// Gets the final absolute resource path.
    /// </summary>
    internal required string Path { get; init; }

    /// <summary>
    /// Gets the temporary file containing the final text.
    /// </summary>
    internal required string TempPath { get; init; }

    /// <summary>
    /// Gets the rollback copy for an existing file, or null for a new file.
    /// </summary>
    internal string? BackupPath { get; init; }

    /// <summary>
    /// Gets the final source text written by the edit.
    /// </summary>
    internal required SourceText Text { get; init; }

    /// <summary>
    /// Gets whether the target must be created instead of replaced.
    /// </summary>
    internal required bool IsNew { get; init; }
}
