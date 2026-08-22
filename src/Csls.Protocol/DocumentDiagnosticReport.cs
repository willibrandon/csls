namespace Csls.Protocol;

/// <summary>
/// Returns a complete or unchanged pull-diagnostic result for one document.
/// </summary>
public sealed record DocumentDiagnosticReport
{
    /// <summary>
    /// Gets either full or unchanged according to the LSP diagnostic report shape.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the opaque identifier for future unchanged-result requests.
    /// </summary>
    public string? ResultId { get; init; }

    /// <summary>
    /// Gets all current findings when the report kind is full.
    /// </summary>
    public IReadOnlyList<Diagnostic>? Items { get; init; }
}
