namespace Csls.Protocol;

/// <summary>
/// Deletes one file or directory before later workspace-edit operations are applied.
/// </summary>
public sealed record DeleteFile : WorkspaceDocumentChange
{
    private readonly string _kind = "delete";

    /// <summary>
    /// Gets the LSP resource-operation discriminator.
    /// </summary>
    public string Kind => _kind;

    /// <summary>
    /// Gets the URI of the resource to delete.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets optional missing-resource and recursive behavior for the delete operation.
    /// </summary>
    public DeleteFileOptions? Options { get; init; }

    /// <summary>
    /// Gets the optional workspace-edit change annotation identifier.
    /// </summary>
    public string? AnnotationId { get; init; }
}
