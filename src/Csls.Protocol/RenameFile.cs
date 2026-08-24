namespace Csls.Protocol;

/// <summary>
/// Renames one file or directory before later workspace-edit operations are applied.
/// </summary>
public sealed record RenameFile : WorkspaceDocumentChange
{
    private readonly string _kind = "rename";

    /// <summary>
    /// Gets the LSP resource-operation discriminator.
    /// </summary>
    public string Kind => _kind;

    /// <summary>
    /// Gets the URI of the existing resource.
    /// </summary>
    public required DocumentUri OldUri { get; init; }

    /// <summary>
    /// Gets the URI assigned to the renamed resource.
    /// </summary>
    public required DocumentUri NewUri { get; init; }

    /// <summary>
    /// Gets optional collision behavior for the rename operation.
    /// </summary>
    public RenameFileOptions? Options { get; init; }

    /// <summary>
    /// Gets the optional workspace-edit change annotation identifier.
    /// </summary>
    public string? AnnotationId { get; init; }
}
