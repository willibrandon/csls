namespace Csls.Protocol;

/// <summary>
/// Identifies one file or folder renamed by the client.
/// </summary>
public sealed record FileRename
{
    /// <summary>
    /// Gets the original file or folder URI.
    /// </summary>
    public required DocumentUri OldUri { get; init; }

    /// <summary>
    /// Gets the new file or folder URI.
    /// </summary>
    public required DocumentUri NewUri { get; init; }
}
