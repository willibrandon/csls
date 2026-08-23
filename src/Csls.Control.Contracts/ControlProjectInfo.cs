namespace Csls.Control.Contracts;

/// <summary>
/// Describes one loaded Roslyn project exposed by the control protocol.
/// </summary>
public sealed class ControlProjectInfo
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
    /// Gets the number of configured analyzer references.
    /// </summary>
    public int AnalyzerReferenceCount { get; init; }
}
