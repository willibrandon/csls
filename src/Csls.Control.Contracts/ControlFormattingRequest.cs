using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one source document and the preferences for formatting preview.
/// </summary>
public sealed record ControlFormattingRequest
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the editor indentation and final-line preferences.
    /// </summary>
    public required FormattingOptions Options { get; init; }
}
