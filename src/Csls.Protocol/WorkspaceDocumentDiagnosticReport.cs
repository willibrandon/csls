using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Returns one complete or unchanged document result from a workspace diagnostic pull.
/// </summary>
public sealed record WorkspaceDocumentDiagnosticReport
{
    /// <summary>
    /// Gets either full or unchanged according to the LSP diagnostic report shape.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the document URI associated with this diagnostic result.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the open document version, or null for a persisted document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Version { get; init; }

    /// <summary>
    /// Gets the opaque identifier for future unchanged-result requests.
    /// </summary>
    public string? ResultId { get; init; }

    /// <summary>
    /// Gets all current findings when the report kind is full.
    /// </summary>
    public IReadOnlyList<Diagnostic>? Items { get; init; }
}
