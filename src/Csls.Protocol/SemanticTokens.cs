namespace Csls.Protocol;

/// <summary>
/// Contains a complete relative-encoded semantic-token sequence.
/// </summary>
public sealed record SemanticTokens
{
    /// <summary>
    /// Gets the opaque identifier used by a subsequent delta request.
    /// </summary>
    public string? ResultId { get; init; }

    /// <summary>
    /// Gets the five-integer relative encoding for every ordered semantic token.
    /// </summary>
    public required IReadOnlyList<int> Data { get; init; }
}
