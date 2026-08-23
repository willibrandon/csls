namespace Csls.Protocol;

/// <summary>
/// Describes the server's inlay-hint resolve behavior.
/// </summary>
public sealed record InlayHintOptions
{
    /// <summary>
    /// Gets whether the server resolves deferred hint details.
    /// </summary>
    public bool ResolveProvider { get; init; }
}
