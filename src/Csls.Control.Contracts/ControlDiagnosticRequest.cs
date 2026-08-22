namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one document and optional prior result for a control diagnostic request.
/// </summary>
public sealed class ControlDiagnosticRequest
{
    /// <summary>
    /// Gets the absolute path of the target document.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the prior opaque result identifier for unchanged-result detection.
    /// </summary>
    public string? PreviousResultId { get; init; }
}
