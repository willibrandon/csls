namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies one operation in a language-neutral debugger expression tree.
/// </summary>
public enum DebugExpressionNodeKind
{
    /// <summary>
    /// Reads a local or argument.
    /// </summary>
    Identifier,

    /// <summary>
    /// Reads the current instance receiver.
    /// </summary>
    This,

    /// <summary>
    /// Produces a compiler-decoded literal.
    /// </summary>
    Literal,

    /// <summary>
    /// Applies one explicit built-in CLR conversion to its child.
    /// </summary>
    Conversion,

    /// <summary>
    /// Reads an instance member from its receiver child.
    /// </summary>
    MemberAccess,

    /// <summary>
    /// Reads an indexed element from its receiver and index children.
    /// </summary>
    ElementAccess,

    /// <summary>
    /// Invokes one instance or loaded-type static method on its receiver and arguments.
    /// </summary>
    Invocation,

    /// <summary>
    /// Constructs one loaded non-generic runtime type through a selected constructor.
    /// </summary>
    ObjectCreation,

    /// <summary>
    /// Applies one unary operation to its child.
    /// </summary>
    Unary,

    /// <summary>
    /// Applies one binary operation to its two children.
    /// </summary>
    Binary,

    /// <summary>
    /// Selects one of two values using a Boolean condition.
    /// </summary>
    Conditional,

    /// <summary>
    /// Produces the default value of a type supplied by the surrounding expression context.
    /// </summary>
    DefaultLiteral,

    /// <summary>
    /// Checks an existing value against a named runtime type without allocating or executing code.
    /// </summary>
    TypeTest,

    /// <summary>
    /// Converts an existing reference or produces a typed null when its runtime type is incompatible.
    /// </summary>
    TryCast,

    /// <summary>
    /// Applies an explicit reference cast without numeric or user-defined conversion.
    /// </summary>
    ReferenceCast,

    /// <summary>
    /// Applies a source-language upcast requiring an implicit reference conversion.
    /// </summary>
    ReferenceUpcast
}
