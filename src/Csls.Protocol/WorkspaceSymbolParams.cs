namespace Csls.Protocol;

/// <summary>
/// Carries the client pattern used to search workspace declarations.
/// </summary>
public sealed record WorkspaceSymbolParams
{
    /// <summary>
    /// Gets the declaration search pattern.
    /// </summary>
    public required string Query { get; init; }
}
