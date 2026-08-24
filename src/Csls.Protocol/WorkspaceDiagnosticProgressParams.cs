using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Carries one workspace diagnostic partial result through an LSP progress notification.
/// </summary>
public sealed record WorkspaceDiagnosticProgressParams
{
    /// <summary>
    /// Gets the client-provided partial result token.
    /// </summary>
    public required JsonElement Token { get; init; }

    /// <summary>
    /// Gets the next bounded batch of workspace document reports.
    /// </summary>
    public required WorkspaceDiagnosticReport Value { get; init; }
}
