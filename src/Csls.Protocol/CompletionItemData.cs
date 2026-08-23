namespace Csls.Protocol;

/// <summary>
/// Identifies one completion candidate for deterministic lazy resolution.
/// </summary>
public sealed record CompletionItemData
{
    /// <summary>
    /// Gets the source document that produced the completion item.
    /// </summary>
    public required DocumentUri DocumentUri { get; init; }

    /// <summary>
    /// Gets the source position that produced the completion item.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the original completion trigger context.
    /// </summary>
    public CompletionContext? Context { get; init; }

    /// <summary>
    /// Gets the immutable workspace generation used for completion.
    /// </summary>
    public long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the candidate index in the deterministically ordered result.
    /// </summary>
    public int ItemIndex { get; init; }

    /// <summary>
    /// Gets the exact display label used to validate recomputation.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the exact sort text used to validate recomputation.
    /// </summary>
    public required string SortText { get; init; }

    /// <summary>
    /// Gets the exact filter text used to validate recomputation.
    /// </summary>
    public required string FilterText { get; init; }

    /// <summary>
    /// Gets the original Roslyn completion span start.
    /// </summary>
    public int SpanStart { get; init; }

    /// <summary>
    /// Gets the original Roslyn completion span length.
    /// </summary>
    public int SpanLength { get; init; }
}
