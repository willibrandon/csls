namespace Csls.Protocol;

/// <summary>
/// Describes the language server implementation and version.
/// </summary>
public sealed record ServerInfo
{
    /// <summary>
    /// Gets the server name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the server version.
    /// </summary>
    public string? Version { get; init; }
}
