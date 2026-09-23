namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the source-language grammar used to bind a debugger expression.
/// </summary>
public enum DebugExpressionLanguage
{
    /// <summary>
    /// Uses the language-neutral CLR expression grammar.
    /// </summary>
    Common,

    /// <summary>
    /// Uses the C# expression grammar.
    /// </summary>
    CSharp,

    /// <summary>
    /// Uses the Visual Basic expression grammar.
    /// </summary>
    VisualBasic,

    /// <summary>
    /// Uses the F# expression grammar.
    /// </summary>
    FSharp
}
