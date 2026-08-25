namespace Csls.Workspaces;

/// <summary>
/// Describes one completed project during a real workspace load.
/// </summary>
public sealed record WorkspaceLoadProgress
{
    /// <summary>
    /// Gets the display name of the completed project.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets the number of distinct projects completed so far.
    /// </summary>
    public required int CompletedProjects { get; init; }

    /// <summary>
    /// Gets the current project total, including newly discovered references.
    /// </summary>
    public required int TotalProjects { get; init; }

    /// <summary>
    /// Gets the monotonic completion percentage from zero through one hundred.
    /// </summary>
    public required int Percentage { get; init; }
}
