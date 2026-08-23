using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one source symbol and the replacement identifier for rename preview.
/// </summary>
public sealed record ControlRenameRequest
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 symbol position.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the requested replacement identifier.
    /// </summary>
    public required string NewName { get; init; }
}
