namespace Csls.Protocol;

/// <summary>
/// Identifies one file or folder created by the client.
/// </summary>
public sealed record FileCreate
{
    /// <summary>
    /// Gets the created file or folder URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }
}
