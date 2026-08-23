namespace Csls.Protocol;

/// <summary>
/// Advertises the concrete code-action categories implemented by the server.
/// </summary>
public sealed record CodeActionOptions
{
    /// <summary>
    /// Gets the supported code-action categories.
    /// </summary>
    public required IReadOnlyList<string> CodeActionKinds { get; init; }

    /// <summary>
    /// Gets whether code-action results can be resolved lazily.
    /// </summary>
    public bool ResolveProvider { get; init; }
}
