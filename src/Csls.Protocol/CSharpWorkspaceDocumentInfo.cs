namespace Csls.Protocol;

/// <summary>
/// Describes one loaded source document for an editor client.
/// </summary>
public sealed class CSharpWorkspaceDocumentInfo
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
    /// Gets the stable Roslyn identifier of the owning project.
    /// </summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// Gets whether the editor owns an open overlay for the document.
    /// </summary>
    public bool IsOpen { get; init; }
}
