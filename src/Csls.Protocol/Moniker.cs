namespace Csls.Protocol;

/// <summary>
/// Identifies one C# symbol across documents, projects, and indexed repositories.
/// </summary>
public sealed record Moniker
{
    /// <summary>
    /// Gets the namespace that defines the identifier format.
    /// </summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// Gets the opaque symbol identifier defined by the scheme.
    /// </summary>
    public required string Identifier { get; init; }

    /// <summary>
    /// Gets the scope in which the identifier is unique.
    /// </summary>
    public required UniquenessLevel Unique { get; init; }

    /// <summary>
    /// Gets how the symbol participates in the current project.
    /// </summary>
    public MonikerKind? Kind { get; init; }
}
