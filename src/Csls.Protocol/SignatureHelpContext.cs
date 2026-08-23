namespace Csls.Protocol;

/// <summary>
/// Describes the client state that triggered a signature help request.
/// </summary>
public sealed record SignatureHelpContext
{
    /// <summary>
    /// Gets the request trigger category.
    /// </summary>
    public required SignatureHelpTriggerKind TriggerKind { get; init; }

    /// <summary>
    /// Gets the configured character that triggered the request.
    /// </summary>
    public string? TriggerCharacter { get; init; }

    /// <summary>
    /// Gets whether signature help was already active.
    /// </summary>
    public bool IsRetrigger { get; init; }

    /// <summary>
    /// Gets the prior active signature help state.
    /// </summary>
    public SignatureHelp? ActiveSignatureHelp { get; init; }
}
