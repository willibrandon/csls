namespace Csls.Protocol;

/// <summary>
/// Describes the token types and modifier bit positions used by semantic-token results.
/// </summary>
public sealed record SemanticTokensLegend
{
    /// <summary>
    /// Gets the ordered semantic-token type names indexed by result data.
    /// </summary>
    public required IReadOnlyList<string> TokenTypes { get; init; }

    /// <summary>
    /// Gets the ordered semantic-token modifier names represented as result bit flags.
    /// </summary>
    public required IReadOnlyList<string> TokenModifiers { get; init; }
}
