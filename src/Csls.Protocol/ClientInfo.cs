namespace Csls.Protocol;

/// <summary>
/// Describes the editor or tool connected to the language server.
/// </summary>
public sealed record ClientInfo
{
    /// <summary>
    /// Gets the client name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the optional client version.
    /// </summary>
    public string? Version { get; init; }
}
