namespace Csls.Protocol;

/// <summary>
/// Advertises whether document-link targets require a later resolve request.
/// </summary>
public sealed record DocumentLinkOptions
{
    /// <summary>
    /// Gets whether document-link results can be resolved lazily.
    /// </summary>
    public bool ResolveProvider { get; init; }
}
