namespace Csls.Protocol;

/// <summary>
/// Describes one source declaration annotation resolved on demand by the client.
/// </summary>
public sealed record CodeLens
{
    /// <summary>
    /// Gets the single-line declaration range associated with the annotation.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the executable editor command populated during resolve.
    /// </summary>
    public LspCommand? Command { get; init; }

    /// <summary>
    /// Gets immutable server coordinates preserved for deferred resolution.
    /// </summary>
    public CodeLensData? Data { get; init; }
}
