namespace Csls.Protocol;

/// <summary>
/// Identifies one document and the positions requiring syntax selection hierarchies.
/// </summary>
public sealed class SelectionRangeParams
{
    /// <summary>
    /// Gets the target source document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the ordered UTF-16 positions to resolve.
    /// </summary>
    public required IReadOnlyList<Position> Positions { get; init; }
}
