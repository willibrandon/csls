namespace Csls.Control.Contracts;

/// <summary>
/// Describes the exact resource state required before a control edit plan is applied.
/// </summary>
public sealed record ControlResourcePrecondition
{
    /// <summary>
    /// Gets the absolute workspace resource path.
    /// </summary>
    public required string ResourcePath { get; init; }

    /// <summary>
    /// Gets whether the resource must exist when the edit is applied.
    /// </summary>
    public required bool Exists { get; init; }

    /// <summary>
    /// Gets the open-document version, or null for a closed resource.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>
    /// Gets the lowercase SHA-256 content hash required for an existing text resource.
    /// </summary>
    public string? Sha256 { get; init; }
}
