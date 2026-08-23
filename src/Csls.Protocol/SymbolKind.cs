namespace Csls.Protocol;

/// <summary>
/// Identifies the editor-visible category of a program symbol.
/// </summary>
public enum SymbolKind
{
    /// <summary>
    /// Indicates no more specific symbol category.
    /// </summary>
    None = 0,

    /// <summary>
    /// Identifies a file.
    /// </summary>
    File = 1,

    /// <summary>
    /// Identifies a module.
    /// </summary>
    Module = 2,

    /// <summary>
    /// Identifies a namespace.
    /// </summary>
    Namespace = 3,

    /// <summary>
    /// Identifies a package.
    /// </summary>
    Package = 4,

    /// <summary>
    /// Identifies a class.
    /// </summary>
    Class = 5,

    /// <summary>
    /// Identifies a method.
    /// </summary>
    Method = 6,

    /// <summary>
    /// Identifies a property.
    /// </summary>
    Property = 7,

    /// <summary>
    /// Identifies a field.
    /// </summary>
    Field = 8,

    /// <summary>
    /// Identifies a constructor.
    /// </summary>
    Constructor = 9,

    /// <summary>
    /// Identifies an enumeration.
    /// </summary>
    Enum = 10,

    /// <summary>
    /// Identifies an interface.
    /// </summary>
    Interface = 11,

    /// <summary>
    /// Identifies a function.
    /// </summary>
    Function = 12,

    /// <summary>
    /// Identifies a variable.
    /// </summary>
    Variable = 13,

    /// <summary>
    /// Identifies a constant.
    /// </summary>
    Constant = 14,

    /// <summary>
    /// Identifies a string value.
    /// </summary>
    StringValue = 15,

    /// <summary>
    /// Identifies a numeric value.
    /// </summary>
    Number = 16,

    /// <summary>
    /// Identifies a Boolean value.
    /// </summary>
    Boolean = 17,

    /// <summary>
    /// Identifies an array value.
    /// </summary>
    Array = 18,

    /// <summary>
    /// Identifies an object value.
    /// </summary>
    ObjectValue = 19,

    /// <summary>
    /// Identifies a key.
    /// </summary>
    Key = 20,

    /// <summary>
    /// Identifies a null value.
    /// </summary>
    Null = 21,

    /// <summary>
    /// Identifies an enumeration member.
    /// </summary>
    EnumMember = 22,

    /// <summary>
    /// Identifies a structure.
    /// </summary>
    Struct = 23,

    /// <summary>
    /// Identifies an event.
    /// </summary>
    Event = 24,

    /// <summary>
    /// Identifies an operator.
    /// </summary>
    Operator = 25,

    /// <summary>
    /// Identifies a type parameter.
    /// </summary>
    TypeParameter = 26
}
