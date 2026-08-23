namespace Csls.Protocol;

/// <summary>
/// Contains either delta edits or a complete fallback semantic-token sequence.
/// </summary>
public sealed record SemanticTokensDeltaResult
{
    /// <summary>
    /// Gets the opaque identifier used by a subsequent delta request.
    /// </summary>
    public string? ResultId { get; init; }

    /// <summary>
    /// Gets complete replacement data when the prior result is unavailable.
    /// </summary>
    public IReadOnlyList<int>? Data { get; init; }

    /// <summary>
    /// Gets edits that transform the prior result into the current result.
    /// </summary>
    public IReadOnlyList<SemanticTokensEdit>? Edits { get; init; }
}
