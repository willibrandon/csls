using Microsoft.CodeAnalysis;

namespace Csls.Workspaces;

/// <summary>
/// Holds one loaded Roslyn workspace, its current solution, and ownership root.
/// </summary>
public sealed record WorkspaceFolderSnapshot
{
    /// <summary>
    /// Gets the absolute directory, project, solution, or source-file workspace root.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Gets the Roslyn workspace retained until replacement or shutdown.
    /// </summary>
    public required Workspace Workspace { get; init; }

    /// <summary>
    /// Gets the immutable Roslyn solution published for language-feature requests.
    /// </summary>
    public required Solution Solution { get; init; }
}
