namespace Csls.Protocol;

/// <summary>
/// Requests current diagnostics for one versioned workspace document.
/// </summary>
public sealed record DocumentDiagnosticParams
{
    /// <summary>
    /// Gets the document whose compiler and analyzer findings are requested.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the optional diagnostic provider identifier selected by the client.
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>
    /// Gets the prior opaque result identifier for unchanged-result detection.
    /// </summary>
    public string? PreviousResultId { get; init; }
}
