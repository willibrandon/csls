namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies one language-neutral debugger expression operator.
/// </summary>
public enum DebugExpressionOperator
{
    /// <summary>
    /// Indicates that a node has no operator.
    /// </summary>
    None,

    /// <summary>
    /// Preserves a numeric operand.
    /// </summary>
    UnaryPlus,

    /// <summary>
    /// Negates a numeric operand.
    /// </summary>
    Negate,

    /// <summary>
    /// Negates a Boolean operand.
    /// </summary>
    LogicalNot,

    /// <summary>
    /// Complements an integral operand.
    /// </summary>
    OnesComplement,

    /// <summary>
    /// Adds numeric operands or concatenates strings.
    /// </summary>
    Add,

    /// <summary>
    /// Subtracts the right numeric operand from the left.
    /// </summary>
    Subtract,

    /// <summary>
    /// Multiplies numeric operands.
    /// </summary>
    Multiply,

    /// <summary>
    /// Divides the left numeric operand by the right.
    /// </summary>
    Divide,

    /// <summary>
    /// Computes the numeric remainder.
    /// </summary>
    Remainder,

    /// <summary>
    /// Compares operands for equality.
    /// </summary>
    Equal,

    /// <summary>
    /// Compares operands for inequality.
    /// </summary>
    NotEqual,

    /// <summary>
    /// Compares whether the left operand is less than the right.
    /// </summary>
    LessThan,

    /// <summary>
    /// Compares whether the left operand is less than or equal to the right.
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Compares whether the left operand is greater than the right.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Compares whether the left operand is greater than or equal to the right.
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Computes conditional Boolean conjunction.
    /// </summary>
    LogicalAnd,

    /// <summary>
    /// Computes conditional Boolean disjunction.
    /// </summary>
    LogicalOr,

    /// <summary>
    /// Computes integral or Boolean conjunction.
    /// </summary>
    BitwiseAnd,

    /// <summary>
    /// Computes integral or Boolean disjunction.
    /// </summary>
    BitwiseOr,

    /// <summary>
    /// Computes integral or Boolean exclusive disjunction.
    /// </summary>
    ExclusiveOr
}
