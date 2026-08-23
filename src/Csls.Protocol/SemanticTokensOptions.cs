namespace Csls.Protocol;

/// <summary>
/// Advertises the server semantic-token legend and supported request forms.
/// </summary>
public sealed record SemanticTokensOptions
{
    /// <summary>
    /// Gets the ordered token type and modifier legend.
    /// </summary>
    public required SemanticTokensLegend Legend { get; init; }

    /// <summary>
    /// Gets the complete-document semantic-token behavior.
    /// </summary>
    public required SemanticTokensFullOptions Full { get; init; }

    /// <summary>
    /// Gets whether range-scoped semantic-token requests are supported.
    /// </summary>
    public bool Range { get; init; }
}
