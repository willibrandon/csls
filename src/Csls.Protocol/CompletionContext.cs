namespace Csls.Protocol;

/// <summary>
/// Describes how the client triggered one completion request.
/// </summary>
public sealed record CompletionContext
{
    /// <summary>
    /// Gets the completion trigger category.
    /// </summary>
    public CompletionTriggerKind TriggerKind { get; init; }

    /// <summary>
    /// Gets the exact trigger character when one caused the request.
    /// </summary>
    public string? TriggerCharacter { get; init; }
}
