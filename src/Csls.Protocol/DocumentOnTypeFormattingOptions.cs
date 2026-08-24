namespace Csls.Protocol;

/// <summary>
/// Advertises the characters that trigger formatting while a document is edited.
/// </summary>
public sealed record DocumentOnTypeFormattingOptions
{
    /// <summary>
    /// Gets the primary formatting trigger character.
    /// </summary>
    public required string FirstTriggerCharacter { get; init; }

    /// <summary>
    /// Gets the additional formatting trigger characters.
    /// </summary>
    public IReadOnlyList<string>? MoreTriggerCharacter { get; init; }
}
