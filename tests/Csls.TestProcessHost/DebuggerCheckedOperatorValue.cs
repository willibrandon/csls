namespace Csls.TestProcessHost;

/// <summary>
/// Exposes compiler-authored regular and checked operators to debugger evaluation.
/// </summary>
internal sealed class DebuggerCheckedOperatorValue
{
    private readonly int _number;

    /// <summary>
    /// Initializes an authored operator operand.
    /// </summary>
    /// <param name="number">The value observed by the operator.</param>
    internal DebuggerCheckedOperatorValue(int number) => _number = number;

    /// <summary>
    /// Applies the regular addition operator.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The regular addition marker.</returns>
    public static int operator +(
        DebuggerCheckedOperatorValue left,
        DebuggerCheckedOperatorValue right) => left._number + right._number + 1000;

    /// <summary>
    /// Applies the checked addition operator.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The checked addition marker.</returns>
    public static int operator checked +(
        DebuggerCheckedOperatorValue left,
        DebuggerCheckedOperatorValue right) => left._number + right._number + 2000;

    /// <summary>
    /// Applies an unpaired regular addition operator.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The object operand.</param>
    /// <returns>The unpaired regular addition marker.</returns>
    public static int operator +(DebuggerCheckedOperatorValue left, object right)
    {
        GC.KeepAlive(right);
        return left._number + 3000;
    }

    /// <summary>
    /// Applies the narrowest constant-compatible addition operator.
    /// </summary>
    /// <param name="left">The authored operand.</param>
    /// <param name="right">The byte operand.</param>
    /// <returns>The byte-overload marker.</returns>
    public static int operator +(DebuggerCheckedOperatorValue left, byte right) =>
        left._number + right + 8000;

    /// <summary>
    /// Applies the wider constant-compatible addition operator.
    /// </summary>
    /// <param name="left">The authored operand.</param>
    /// <param name="right">The short operand.</param>
    /// <returns>The short-overload marker.</returns>
    public static int operator +(DebuggerCheckedOperatorValue left, short right) =>
        left._number + right + 9000;

    /// <summary>
    /// Applies a numeric-widening addition operator.
    /// </summary>
    /// <param name="left">The authored operand.</param>
    /// <param name="right">The widened long operand.</param>
    /// <returns>The long-overload marker.</returns>
    public static int operator +(DebuggerCheckedOperatorValue left, long right) =>
        checked((int)(left._number + right + 10000));

    /// <summary>
    /// Applies the regular division operator.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The regular division marker.</returns>
    public static int operator /(
        DebuggerCheckedOperatorValue left,
        DebuggerCheckedOperatorValue right) => left._number + right._number + 4000;

    /// <summary>
    /// Applies the checked division operator.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The checked division marker.</returns>
    public static int operator checked /(
        DebuggerCheckedOperatorValue left,
        DebuggerCheckedOperatorValue right) => left._number + right._number + 5000;

    /// <summary>
    /// Applies the regular unary negation operator.
    /// </summary>
    /// <param name="value">The operand.</param>
    /// <returns>The regular unary marker.</returns>
    public static int operator -(DebuggerCheckedOperatorValue value) =>
        value._number + 6000;

    /// <summary>
    /// Applies the checked unary negation operator.
    /// </summary>
    /// <param name="value">The operand.</param>
    /// <returns>The checked unary marker.</returns>
    public static int operator checked -(DebuggerCheckedOperatorValue value) =>
        value._number + 7000;

    /// <summary>
    /// Runs compiler-selected regular addition.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerAdd(DebuggerCheckedOperatorValue value) => value + value;

    /// <summary>
    /// Runs compiler-selected checked addition.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerCheckedAdd(DebuggerCheckedOperatorValue value) =>
        checked(value + value);

    /// <summary>
    /// Runs compiler-selected checked fallback addition.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <param name="other">The object operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerCheckedFallback(
        DebuggerCheckedOperatorValue value,
        object other) => checked(value + other);

    /// <summary>
    /// Runs compiler-selected constant addition.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerConstantAdd(DebuggerCheckedOperatorValue value) =>
        checked(value + 1);

    /// <summary>
    /// Runs compiler-selected numeric-widening addition.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <param name="right">The integer operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerNumericAdd(
        DebuggerCheckedOperatorValue value,
        int right) => value + right;

    /// <summary>
    /// Runs compiler-selected regular division.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerDivide(DebuggerCheckedOperatorValue value) => value / value;

    /// <summary>
    /// Runs compiler-selected checked division.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerCheckedDivide(DebuggerCheckedOperatorValue value) =>
        checked(value / value);

    /// <summary>
    /// Runs compiler-selected regular unary negation.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerNegate(DebuggerCheckedOperatorValue value) => -value;

    /// <summary>
    /// Runs compiler-selected checked unary negation.
    /// </summary>
    /// <param name="value">The authored operand.</param>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerCheckedNegate(DebuggerCheckedOperatorValue value) =>
        checked(-value);
}
