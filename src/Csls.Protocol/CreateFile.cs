namespace Csls.Protocol;

/// <summary>
/// Creates one file or directory before later workspace-edit operations are applied.
/// </summary>
public sealed record CreateFile : WorkspaceDocumentChange
{
    private readonly string _kind = "create";

    /// <summary>
    /// Gets the LSP resource-operation discriminator.
    /// </summary>
    public string Kind => _kind;

    /// <summary>
    /// Gets the URI of the resource to create.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets optional collision behavior for the create operation.
    /// </summary>
    public CreateFileOptions? Options { get; init; }

    /// <summary>
    /// Gets the optional workspace-edit change annotation identifier.
    /// </summary>
    public string? AnnotationId { get; init; }
}
