namespace Csls.Protocol;

/// <summary>
/// Advertises the characters that trigger or retrigger signature help.
/// </summary>
public sealed record SignatureHelpOptions
{
    /// <summary>
    /// Gets the characters that initially trigger signature help.
    /// </summary>
    public IReadOnlyList<string>? TriggerCharacters { get; init; }

    /// <summary>
    /// Gets the characters that retrigger active signature help.
    /// </summary>
    public IReadOnlyList<string>? RetriggerCharacters { get; init; }
}
