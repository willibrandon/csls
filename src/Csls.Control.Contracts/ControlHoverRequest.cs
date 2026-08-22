using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies an absolute document path and UTF-16 position for a control hover request.
/// </summary>
public sealed class ControlHoverRequest
{
    /// <summary>
    /// Gets the absolute path of the document in the active workspace.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 document position.
    /// </summary>
    public required Position Position { get; init; }
}
