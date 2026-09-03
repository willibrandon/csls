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
    Conditional
}
