using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one document and ordered positions for syntax selection hierarchies.
/// </summary>
public sealed class ControlSelectionRangeRequest
{
    /// <summary>
    /// Gets the absolute path of the target document.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the ordered zero-based UTF-16 positions.
    /// </summary>
    public required IReadOnlyList<Position> Positions { get; init; }
}
