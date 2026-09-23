namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the debugger-facing category of one expression completion.
/// </summary>
public enum DebugCompletionItemKind
{
    /// <summary>
    /// Identifies a local variable or argument.
    /// </summary>
    Variable,

    /// <summary>
    /// Identifies an instance field.
    /// </summary>
    Field,

    /// <summary>
    /// Identifies an invocable managed method.
    /// </summary>
    Method,

    /// <summary>
    /// Identifies a source-language keyword or literal keyword.
    /// </summary>
    Keyword
}
