using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Identifies a text document with an optional open-document version precondition.
/// </summary>
public sealed record OptionalVersionedTextDocumentIdentifier
{
    /// <summary>
    /// Gets the target text document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the required client document version, or null for a closed document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Version { get; init; }
}
