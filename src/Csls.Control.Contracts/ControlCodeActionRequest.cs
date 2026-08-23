using LspRange = Csls.Protocol.Range;

namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one source range and the code-action categories to preview.
/// </summary>
public sealed record ControlCodeActionRequest
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the zero-based UTF-16 source range.
    /// </summary>
    public required LspRange Range { get; init; }

    /// <summary>
    /// Gets the optional requested code-action categories.
    /// </summary>
    public IReadOnlyList<string>? Only { get; init; }
}
