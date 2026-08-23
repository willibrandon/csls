namespace Csls.Protocol;

/// <summary>
/// Identifies the semantic editor icon category for one completion item.
/// </summary>
public enum CompletionItemKind
{
    /// <summary>
    /// Indicates no more specific completion kind.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates ordinary text.
    /// </summary>
    Text = 1,

    /// <summary>
    /// Indicates a method.
    /// </summary>
    Method = 2,

    /// <summary>
    /// Indicates a function.
    /// </summary>
    Function = 3,

    /// <summary>
    /// Indicates a constructor.
    /// </summary>
    Constructor = 4,

    /// <summary>
    /// Indicates a field.
    /// </summary>
    Field = 5,

    /// <summary>
    /// Indicates a variable.
    /// </summary>
    Variable = 6,

    /// <summary>
    /// Indicates a class.
    /// </summary>
    Class = 7,

    /// <summary>
    /// Indicates an interface.
    /// </summary>
    Interface = 8,

    /// <summary>
    /// Indicates a module or namespace.
    /// </summary>
    Module = 9,

    /// <summary>
    /// Indicates a property.
    /// </summary>
    Property = 10,

    /// <summary>
    /// Indicates a unit value.
    /// </summary>
    Unit = 11,

    /// <summary>
    /// Indicates a value.
    /// </summary>
    Value = 12,

    /// <summary>
    /// Indicates an enumeration.
    /// </summary>
    Enum = 13,

    /// <summary>
    /// Indicates a keyword.
    /// </summary>
    Keyword = 14,

    /// <summary>
    /// Indicates a snippet.
    /// </summary>
    Snippet = 15,

    /// <summary>
    /// Indicates a color value.
    /// </summary>
    Color = 16,

    /// <summary>
    /// Indicates a file.
    /// </summary>
    File = 17,

    /// <summary>
    /// Indicates a reference.
    /// </summary>
    Reference = 18,

    /// <summary>
    /// Indicates a folder.
    /// </summary>
    Folder = 19,

    /// <summary>
    /// Indicates an enumeration member.
    /// </summary>
    EnumMember = 20,

    /// <summary>
    /// Indicates a constant.
    /// </summary>
    Constant = 21,

    /// <summary>
    /// Indicates a structure.
    /// </summary>
    Struct = 22,

    /// <summary>
    /// Indicates an event.
    /// </summary>
    Event = 23,

    /// <summary>
    /// Indicates an operator.
    /// </summary>
    Operator = 24,

    /// <summary>
    /// Indicates a type parameter.
    /// </summary>
    TypeParameter = 25
}
