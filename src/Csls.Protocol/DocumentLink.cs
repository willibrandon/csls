namespace Csls.Protocol;

/// <summary>
/// Identifies one source range that opens an internal or external resource.
/// </summary>
public sealed record DocumentLink
{
    /// <summary>
    /// Gets the exact source range containing the link text.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the absolute resource URI opened by the link.
    /// </summary>
    public DocumentUri? Target { get; init; }

    /// <summary>
    /// Gets optional text displayed while the client hovers over the link.
    /// </summary>
    public string? Tooltip { get; init; }
}
