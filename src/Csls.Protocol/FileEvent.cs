namespace Csls.Protocol;

/// <summary>
/// Describes one file-system change reported by an LSP client.
/// </summary>
public sealed record FileEvent
{
    /// <summary>
    /// Gets the changed file URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the kind of file-system change.
    /// </summary>
    public required FileChangeType Type { get; init; }
}
