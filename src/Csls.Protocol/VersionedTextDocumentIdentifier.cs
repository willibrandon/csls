namespace Csls.Protocol;

/// <summary>
/// Identifies a text document and the client version after a content mutation.
/// </summary>
public sealed record VersionedTextDocumentIdentifier
{
    /// <summary>
    /// Gets the document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the monotonically increasing client document version.
    /// </summary>
    public int Version { get; init; }
}
