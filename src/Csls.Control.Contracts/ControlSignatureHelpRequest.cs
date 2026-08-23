using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one absolute document position for signature help.
/// </summary>
public sealed record ControlSignatureHelpRequest
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 document position.
    /// </summary>
    public required Position Position { get; init; }
}
