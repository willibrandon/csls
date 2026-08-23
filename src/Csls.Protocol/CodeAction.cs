namespace Csls.Protocol;

/// <summary>
/// Describes one editor-visible source transformation and its concrete edit.
/// </summary>
public sealed record CodeAction
{
    /// <summary>
    /// Gets the editor-visible action title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the hierarchical code-action category.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Gets diagnostics addressed by this action.
    /// </summary>
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }

    /// <summary>
    /// Gets whether this action is preferred over equivalent alternatives.
    /// </summary>
    public bool? IsPreferred { get; init; }

    /// <summary>
    /// Gets the concrete version-aware workspace edit.
    /// </summary>
    public WorkspaceEdit? Edit { get; init; }
}
