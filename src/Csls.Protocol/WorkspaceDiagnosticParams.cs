using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Requests current diagnostics for every document in the loaded workspace.
/// </summary>
public sealed record WorkspaceDiagnosticParams
{
    /// <summary>
    /// Gets the optional diagnostic provider identifier selected by the client.
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>
    /// Gets the client token used for bounded partial diagnostic results.
    /// </summary>
    public JsonElement? PartialResultToken { get; init; }

    /// <summary>
    /// Gets the document results already retained by the client.
    /// </summary>
    public IReadOnlyList<PreviousResultId> PreviousResultIds { get; init; } = [];
}
