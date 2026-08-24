using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Identifies a document position and the character that triggered formatting.
/// </summary>
public sealed record DocumentOnTypeFormattingParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the position around which formatting should occur.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the character that triggered formatting.
    /// </summary>
    [JsonPropertyName("ch")]
    public required string Character { get; init; }

    /// <summary>
    /// Gets the editor formatting preferences.
    /// </summary>
    public required FormattingOptions Options { get; init; }
}
