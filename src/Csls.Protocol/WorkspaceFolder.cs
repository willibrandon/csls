namespace Csls.Protocol;

/// <summary>
/// Identifies a client workspace folder and its display name.
/// </summary>
public sealed record WorkspaceFolder
{
    /// <summary>
    /// Gets the absolute workspace folder URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the client-provided workspace folder name.
    /// </summary>
    public required string Name { get; init; }
}
