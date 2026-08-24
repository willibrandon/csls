namespace Csls.Protocol;

/// <summary>
/// Publishes the complete diagnostic state for one client document version.
/// </summary>
public sealed record PublishDiagnosticsParams
{
    /// <summary>
    /// Gets the document whose diagnostics are being replaced.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the open document version associated with the diagnostics.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>
    /// Gets the complete current diagnostic collection for the document.
    /// </summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
}
