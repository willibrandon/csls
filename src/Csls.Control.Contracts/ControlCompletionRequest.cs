using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one document and UTF-16 position for a control completion request.
/// </summary>
public sealed class ControlCompletionRequest
{
    /// <summary>
    /// Gets the absolute path of the target document.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 completion position.
    /// </summary>
    public required Position Position { get; init; }
}
