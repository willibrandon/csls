namespace Csls.Protocol;

/// <summary>
/// Advertises completion triggers and optional item-resolution support.
/// </summary>
public sealed record CompletionOptions
{
    /// <summary>
    /// Gets whether completion items can be resolved lazily.
    /// </summary>
    public bool ResolveProvider { get; init; }

    /// <summary>
    /// Gets the characters that should trigger automatic completion.
    /// </summary>
    public IReadOnlyList<string>? TriggerCharacters { get; init; }
}
