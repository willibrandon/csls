namespace Csls.Protocol;

/// <summary>
/// Describes one bounded Roslyn completion candidate and its exact document edits.
/// </summary>
public sealed record CompletionItem
{
    /// <summary>
    /// Gets the editor-visible completion label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the semantic completion category.
    /// </summary>
    public CompletionItemKind? Kind { get; init; }

    /// <summary>
    /// Gets concise supplemental type or namespace information.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets the stable text used for client-side sorting.
    /// </summary>
    public string? SortText { get; init; }

    /// <summary>
    /// Gets the text used for client-side filtering.
    /// </summary>
    public string? FilterText { get; init; }

    /// <summary>
    /// Gets the primary text edit committed for this completion.
    /// </summary>
    public TextEdit? TextEdit { get; init; }

    /// <summary>
    /// Gets additional edits such as a required using directive.
    /// </summary>
    public IReadOnlyList<TextEdit>? AdditionalTextEdits { get; init; }
}
