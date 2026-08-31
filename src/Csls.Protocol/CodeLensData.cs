namespace Csls.Protocol;

/// <summary>
/// Preserves immutable declaration coordinates required to resolve a code lens.
/// </summary>
public sealed record CodeLensData
{
    /// <summary>
    /// Gets the workspace generation that produced the code lens.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Gets the source document containing the declaration.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the exact identifier range of the declaration.
    /// </summary>
    public required Range DeclarationRange { get; init; }
}
