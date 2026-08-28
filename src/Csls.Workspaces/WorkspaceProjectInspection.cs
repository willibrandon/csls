namespace Csls.Workspaces;

/// <summary>
/// Describes one Roslyn project in the inspected workspace generation.
/// </summary>
public sealed class WorkspaceProjectInspection
{
    /// <summary>
    /// Gets the stable Roslyn project identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the display name of the project.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the absolute project file path when one exists.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the owning absolute workspace root.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>
    /// Gets the Roslyn language name.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the number of source documents in the project.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Gets the number of configured analyzer references.
    /// </summary>
    public int AnalyzerReferenceCount { get; init; }

    /// <summary>
    /// Gets the configured analyzer assembly paths in stable order.
    /// </summary>
    public required IReadOnlyList<string> AnalyzerPaths { get; init; }

    /// <summary>
    /// Gets the stable Roslyn identifiers of referenced projects in deterministic order.
    /// </summary>
    public required IReadOnlyList<string> ProjectReferenceIds { get; init; }
}
