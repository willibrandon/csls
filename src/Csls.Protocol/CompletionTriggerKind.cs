namespace Csls.Protocol;

/// <summary>
/// Identifies how an LSP client triggered a completion request.
/// </summary>
public enum CompletionTriggerKind
{
    /// <summary>
    /// Indicates no explicit completion trigger.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates explicit invocation by the client or user.
    /// </summary>
    Invoked = 1,

    /// <summary>
    /// Indicates invocation by a configured trigger character.
    /// </summary>
    TriggerCharacter = 2,

    /// <summary>
    /// Indicates reinvocation after an incomplete completion list.
    /// </summary>
    TriggerForIncompleteCompletions = 3
}
