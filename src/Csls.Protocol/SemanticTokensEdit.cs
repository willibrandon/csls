namespace Csls.Protocol;

/// <summary>
/// Replaces one contiguous region of a prior semantic-token integer sequence.
/// </summary>
public sealed record SemanticTokensEdit
{
    /// <summary>
    /// Gets the zero-based integer-array offset where replacement begins.
    /// </summary>
    public int Start { get; init; }

    /// <summary>
    /// Gets the number of prior integers removed at the replacement offset.
    /// </summary>
    public int DeleteCount { get; init; }

    /// <summary>
    /// Gets the replacement integers, or null when the edit only deletes data.
    /// </summary>
    public IReadOnlyList<int>? Data { get; init; }
}
