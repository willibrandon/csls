namespace Csls.Protocol;

/// <summary>
/// Identifies a workspace symbol document with an optional resolved range.
/// </summary>
public sealed record WorkspaceSymbolLocation
{
    /// <summary>
    /// Gets the source document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the declaration range after resolution.
    /// </summary>
    public Range? Range { get; init; }
}
