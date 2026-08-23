namespace Csls.Control.Contracts;

/// <summary>
/// Carries a bounded workspace declaration search pattern.
/// </summary>
public sealed record ControlWorkspaceSymbolRequest
{
    /// <summary>
    /// Gets the client declaration search pattern.
    /// </summary>
    public required string Query { get; init; }
}
