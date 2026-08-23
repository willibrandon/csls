using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one document position and declaration behavior for source navigation.
/// </summary>
public sealed class ControlNavigationRequest
{
    /// <summary>
    /// Gets the absolute path of the target document.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 symbol position.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets whether reference results include declaration locations.
    /// </summary>
    public bool IncludeDeclaration { get; init; }
}
