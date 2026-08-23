namespace Csls.Protocol;

/// <summary>
/// Defines the portable LSP semantic-token vocabulary emitted for C# documents.
/// </summary>
public static class CSharpSemanticTokensLegend
{
    /// <summary>
    /// Gets the standard token types plus the widely supported label extension.
    /// </summary>
    public static IReadOnlyList<string> TokenTypes { get; } = Array.AsReadOnly(
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "decorator",
        "label"
    ]);

    /// <summary>
    /// Gets the additive modifiers produced by Roslyn semantic classification.
    /// </summary>
    public static IReadOnlyList<string> TokenModifiers { get; } = Array.AsReadOnly(
    [
        "static",
        "deprecated",
        "reassigned"
    ]);

    /// <summary>
    /// Creates an immutable protocol legend using the supported C# vocabulary.
    /// </summary>
    /// <returns>The semantic-token legend advertised during initialization.</returns>
    public static SemanticTokensLegend Create() => new()
    {
        TokenTypes = TokenTypes,
        TokenModifiers = TokenModifiers
    };
}
