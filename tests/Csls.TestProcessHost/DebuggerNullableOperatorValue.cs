namespace Csls.TestProcessHost;

/// <summary>
/// Exposes compiler-authored operators with exact nullable value-type parameters.
/// </summary>
internal readonly struct DebuggerNullableOperatorValue
{
    private readonly int _number;

    /// <summary>
    /// Initializes an authored nullable operator operand.
    /// </summary>
    /// <param name="number">The value observed by the operator.</param>
    internal DebuggerNullableOperatorValue(int number) => _number = number;

    /// <summary>
    /// Applies an operator that receives exact nullable operands.
    /// </summary>
    /// <param name="left">The optional left operand.</param>
    /// <param name="right">The optional right operand.</param>
    /// <returns>A marker that distinguishes populated and empty operands.</returns>
    public static int operator +(
        DebuggerNullableOperatorValue? left,
        DebuggerNullableOperatorValue? right) =>
        11000 + (left.HasValue ? left.Value._number : 100) +
            (right.HasValue ? right.Value._number : 200);

    /// <summary>
    /// Applies a unary operator that receives an exact nullable operand.
    /// </summary>
    /// <param name="value">The optional operand.</param>
    /// <returns>A marker that distinguishes populated and empty operands.</returns>
    public static int operator -(DebuggerNullableOperatorValue? value) =>
        12000 + (value.HasValue ? value.Value._number : 300);

    /// <summary>
    /// Applies a relational operator that is lifted by the C# compiler.
    /// </summary>
    /// <param name="left">The underlying left operand.</param>
    /// <param name="right">The underlying right operand.</param>
    /// <returns>A deliberately reversed comparison result.</returns>
    public static bool operator >(
        DebuggerNullableOperatorValue left,
        DebuggerNullableOperatorValue right) => left._number < right._number;

    /// <summary>
    /// Applies the paired relational operator required by C#.
    /// </summary>
    /// <param name="left">The underlying left operand.</param>
    /// <param name="right">The underlying right operand.</param>
    /// <returns>A deliberately reversed comparison result.</returns>
    public static bool operator <(
        DebuggerNullableOperatorValue left,
        DebuggerNullableOperatorValue right) => left._number > right._number;

    /// <summary>
    /// Applies an arithmetic operator that returns a liftable value type.
    /// </summary>
    /// <param name="left">The underlying left operand.</param>
    /// <param name="right">The underlying right operand.</param>
    /// <returns>A marker derived from both operands.</returns>
    public static int operator *(
        DebuggerNullableOperatorValue left,
        DebuggerNullableOperatorValue right) => (left._number * 100) + right._number;

    /// <summary>
    /// Applies a unary operator that returns a liftable value type.
    /// </summary>
    /// <param name="value">The underlying operand.</param>
    /// <returns>A marker derived from the operand.</returns>
    public static int operator ~(DebuggerNullableOperatorValue value) =>
        13000 + value._number;

    /// <summary>
    /// Applies an operator whose second lifted operand requires numeric widening.
    /// </summary>
    /// <param name="left">The underlying authored operand.</param>
    /// <param name="right">The widened numeric operand.</param>
    /// <returns>A marker derived from both operands.</returns>
    public static int operator %(
        DebuggerNullableOperatorValue left,
        long right) => checked((int)((left._number * 100) + right));

    /// <summary>
    /// Runs compiler-selected binary nullable-parameter addition.
    /// </summary>
    /// <param name="left">The optional left operand.</param>
    /// <param name="right">The optional right operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerAdd(
        DebuggerNullableOperatorValue? left,
        DebuggerNullableOperatorValue? right) => left + right;

    /// <summary>
    /// Runs compiler-selected unary nullable-parameter negation.
    /// </summary>
    /// <param name="value">The optional operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerNegate(DebuggerNullableOperatorValue? value) => -value;

    /// <summary>
    /// Runs compiler-selected lifted nullable relational comparison.
    /// </summary>
    /// <param name="left">The optional left operand.</param>
    /// <param name="right">The optional right operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static bool CompilerGreaterThan(
        DebuggerNullableOperatorValue? left,
        DebuggerNullableOperatorValue? right) => left > right;

    /// <summary>
    /// Runs compiler-selected lifted nullable arithmetic.
    /// </summary>
    /// <param name="left">The optional left operand.</param>
    /// <param name="right">The optional right operand.</param>
    /// <returns>The compiler-selected nullable result.</returns>
    internal static int? CompilerMultiply(
        DebuggerNullableOperatorValue? left,
        DebuggerNullableOperatorValue? right) => left * right;

    /// <summary>
    /// Runs compiler-selected lifted arithmetic with one non-nullable operand.
    /// </summary>
    /// <param name="left">The optional left operand.</param>
    /// <param name="right">The underlying right operand.</param>
    /// <returns>The compiler-selected nullable result.</returns>
    internal static int? CompilerMixedMultiply(
        DebuggerNullableOperatorValue? left,
        DebuggerNullableOperatorValue right) => left * right;

    /// <summary>
    /// Runs compiler-selected lifted nullable unary arithmetic.
    /// </summary>
    /// <param name="value">The optional operand.</param>
    /// <returns>The compiler-selected nullable result.</returns>
    internal static int? CompilerOnesComplement(
        DebuggerNullableOperatorValue? value) => ~value;

    /// <summary>
    /// Runs compiler-selected lifted arithmetic with nullable numeric widening.
    /// </summary>
    /// <param name="left">The optional authored operand.</param>
    /// <param name="right">The optional integer operand.</param>
    /// <returns>The compiler-selected nullable result.</returns>
    internal static int? CompilerNumericRemainder(
        DebuggerNullableOperatorValue? left,
        int? right) => left % right;
}
