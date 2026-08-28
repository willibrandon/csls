namespace Csls.Protocol;

/// <summary>
/// Describes one loaded Roslyn project for an editor client.
/// </summary>
public sealed class CSharpWorkspaceProjectInfo
{
    /// <summary>
    /// Gets the stable Roslyn project identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the project display name.
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
    /// Gets configured analyzer assembly paths in stable order.
    /// </summary>
    public required IReadOnlyList<string> AnalyzerPaths { get; init; }

    /// <summary>
    /// Gets stable identifiers of referenced projects in deterministic order.
    /// </summary>
    public required IReadOnlyList<string> ProjectReferenceIds { get; init; }
}
