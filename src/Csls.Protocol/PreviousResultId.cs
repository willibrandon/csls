namespace Csls.Protocol;

/// <summary>
/// Identifies one workspace document result already retained by an LSP client.
/// </summary>
public sealed record PreviousResultId
{
    /// <summary>
    /// Gets the document URI associated with the retained result.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the opaque result identifier returned by the server.
    /// </summary>
    public required string Value { get; init; }
}
