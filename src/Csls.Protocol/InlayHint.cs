namespace Csls.Protocol;

/// <summary>
/// Describes one type or parameter annotation rendered inside source text.
/// </summary>
public sealed record InlayHint
{
    /// <summary>
    /// Gets the UTF-16 position where the hint is rendered.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the nonempty human-readable hint label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the semantic hint category.
    /// </summary>
    public InlayHintKind? Kind { get; init; }

    /// <summary>
    /// Gets edits applied when the user accepts the hint.
    /// </summary>
    public IReadOnlyList<TextEdit>? TextEdits { get; init; }

    /// <summary>
    /// Gets rich hover information populated during resolve.
    /// </summary>
    public MarkupContent? Tooltip { get; init; }

    /// <summary>
    /// Gets whether editor-colored padding precedes the hint.
    /// </summary>
    public bool? PaddingLeft { get; init; }

    /// <summary>
    /// Gets whether editor-colored padding follows the hint.
    /// </summary>
    public bool? PaddingRight { get; init; }

    /// <summary>
    /// Gets semantic coordinates preserved for late resolve.
    /// </summary>
    public InlayHintData? Data { get; init; }
}
