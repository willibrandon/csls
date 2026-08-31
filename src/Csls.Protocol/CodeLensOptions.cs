namespace Csls.Protocol;

/// <summary>
/// Describes the server's code-lens resolution behavior.
/// </summary>
public sealed record CodeLensOptions
{
    /// <summary>
    /// Gets whether the server resolves deferred code-lens commands.
    /// </summary>
    public bool ResolveProvider { get; init; }
}
