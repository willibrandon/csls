namespace Csls.Protocol;

/// <summary>
/// Advertises complete-document semantic-token behavior.
/// </summary>
public sealed record SemanticTokensFullOptions
{
    /// <summary>
    /// Gets whether clients can request edits relative to a prior complete result.
    /// </summary>
    public bool Delta { get; init; }
}
