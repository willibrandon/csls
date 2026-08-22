namespace Csls.Protocol;

/// <summary>
/// Contains markdown or plain-text content returned to an LSP client.
/// </summary>
public sealed record MarkupContent
{
    /// <summary>
    /// Gets the markup kind, normally markdown or plaintext.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the markup content.
    /// </summary>
    public required string Value { get; init; }
}
