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
}
