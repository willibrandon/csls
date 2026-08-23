namespace Csls.Protocol;

/// <summary>
/// Preserves immutable source coordinates required to resolve a workspace symbol.
/// </summary>
public sealed record WorkspaceSymbolData
{
    /// <summary>
    /// Gets the workspace generation that produced the symbol.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Gets the exact source declaration range.
    /// </summary>
    public required Range Range { get; init; }
}
