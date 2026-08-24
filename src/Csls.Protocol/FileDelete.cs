namespace Csls.Protocol;

/// <summary>
/// Identifies one file or folder deleted by the client.
/// </summary>
public sealed record FileDelete
{
    /// <summary>
    /// Gets the deleted file or folder URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }
}
