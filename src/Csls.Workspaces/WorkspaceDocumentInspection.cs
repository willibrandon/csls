namespace Csls.Workspaces;

/// <summary>
/// Describes one source document in the inspected workspace generation.
/// </summary>
public sealed class WorkspaceDocumentInspection
{
    /// <summary>
    /// Gets the stable Roslyn document identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the source document display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the absolute source path when one exists.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the owning project name.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets whether the editor currently owns an open overlay for the document.
    /// </summary>
    public bool IsOpen { get; init; }
}
