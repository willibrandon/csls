namespace Csls.Protocol;

/// <summary>
/// Contains the client capability values consumed by the language server.
/// </summary>
public sealed record NegotiatedClientCapabilities
{
    /// <summary>
    /// Gets whether completion documentation supports markdown.
    /// </summary>
    public bool CompletionMarkdown { get; init; }

    /// <summary>
    /// Gets whether completion items support snippets.
    /// </summary>
    public bool CompletionSnippets { get; init; }

    /// <summary>
    /// Gets whether hover content supports markdown.
    /// </summary>
    public bool HoverMarkdown { get; init; }

    /// <summary>
    /// Gets whether signature documentation supports markdown.
    /// </summary>
    public bool SignatureMarkdown { get; init; }

    /// <summary>
    /// Gets the maximum number of folding ranges accepted by the client.
    /// </summary>
    public int FoldingRangeLimit { get; init; } = 5_000;

    /// <summary>
    /// Gets whether every folding range must span complete lines.
    /// </summary>
    public bool LineFoldingOnly { get; init; }

    /// <summary>
    /// Gets whether collapsed folding text is supported.
    /// </summary>
    public bool CollapsedFoldingText { get; init; }

    /// <summary>
    /// Gets whether comment folding ranges are supported.
    /// </summary>
    public bool CommentFoldingKind { get; init; } = true;

    /// <summary>
    /// Gets whether import folding ranges are supported.
    /// </summary>
    public bool ImportsFoldingKind { get; init; } = true;

    /// <summary>
    /// Gets whether region folding ranges are supported.
    /// </summary>
    public bool RegionFoldingKind { get; init; } = true;

    /// <summary>
    /// Gets whether the client supports workspace configuration requests.
    /// </summary>
    public bool ConfigurationPull { get; init; }

    /// <summary>
    /// Gets whether workspace edits can create files.
    /// </summary>
    public bool CreateFileWorkspaceEdits { get; init; }

    /// <summary>
    /// Gets whether the client supports diagnostic refresh requests.
    /// </summary>
    public bool DiagnosticRefresh { get; init; }

    /// <summary>
    /// Gets whether the client supports inlay-hint refresh requests.
    /// </summary>
    public bool InlayHintRefresh { get; init; }

    /// <summary>
    /// Gets whether the client supports code-lens refresh requests.
    /// </summary>
    public bool CodeLensRefresh { get; init; }

    /// <summary>
    /// Gets whether the client supports dynamic file watcher registration.
    /// </summary>
    public bool DynamicFileWatching { get; init; }

    /// <summary>
    /// Gets whether the client supports pull diagnostics.
    /// </summary>
    public bool PullDiagnostics { get; init; }

    /// <summary>
    /// Gets whether the client supports work-done progress.
    /// </summary>
    public bool WorkDoneProgress { get; init; }

    /// <summary>
    /// Gets whether the client can resolve CSLS generated and metadata document URIs.
    /// </summary>
    public bool MetadataUris { get; init; }
}
