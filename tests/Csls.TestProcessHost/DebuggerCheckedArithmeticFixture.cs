namespace Csls.TestProcessHost;

/// <summary>
/// Exposes compiler-authored unchecked integer arithmetic for debugger parity comparisons.
/// </summary>
internal static class DebuggerCheckedArithmeticFixture
{
    /// <summary>
    /// Adds a value to the maximum integer with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The value added to the maximum integer.</param>
    /// <returns>The compiler-authored wrapped sum.</returns>
    internal static int AddForDebugger(int value) => unchecked(int.MaxValue + value);

    /// <summary>
    /// Adds a value to the maximum unsigned integer with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The value added to the maximum unsigned integer.</param>
    /// <returns>The compiler-authored wrapped unsigned sum.</returns>
    internal static uint AddUInt32ForDebugger(uint value) => unchecked(uint.MaxValue + value);

    /// <summary>
    /// Adds a value to the maximum long integer with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The value added to the maximum long integer.</param>
    /// <returns>The compiler-authored wrapped long sum.</returns>
    internal static long AddInt64ForDebugger(long value) => unchecked(long.MaxValue + value);

    /// <summary>
    /// Adds a value to the maximum unsigned long with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The value added to the maximum unsigned long.</param>
    /// <returns>The compiler-authored wrapped unsigned long sum.</returns>
    internal static ulong AddUInt64ForDebugger(ulong value) => unchecked(ulong.MaxValue + value);

    /// <summary>
    /// Subtracts a value from the minimum integer with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The value subtracted from the minimum integer.</param>
    /// <returns>The compiler-authored wrapped difference.</returns>
    internal static int SubtractForDebugger(int value) => unchecked(int.MinValue - value);

    /// <summary>
    /// Multiplies a large integer by a value with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The large integer multiplier.</param>
    /// <returns>The compiler-authored wrapped product.</returns>
    internal static int MultiplyForDebugger(int value) => unchecked(100_000_000 * value);

    /// <summary>
    /// Negates a value with unchecked compiler semantics.
    /// </summary>
    /// <param name="value">The integer to negate.</param>
    /// <returns>The compiler-authored wrapped negation.</returns>
    internal static int NegateForDebugger(int value) => unchecked(-value);
}
