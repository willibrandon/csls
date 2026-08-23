namespace Csls.Protocol;

/// <summary>
/// Preserves immutable semantic coordinates required to resolve an inlay hint.
/// </summary>
public sealed record InlayHintData
{
    /// <summary>
    /// Gets the workspace generation that produced the hint.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Gets the source document containing the hint.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the source range whose semantics produced the hint.
    /// </summary>
    public required Range SourceRange { get; init; }

    /// <summary>
    /// Gets the server-owned hint data category.
    /// </summary>
    public required InlayHintDataKind Kind { get; init; }
}
