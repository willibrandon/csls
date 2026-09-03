namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable instance fields for debugger expansion tests.
/// </summary>
internal sealed class DebuggerFixtureValue
{
    /// <summary>
    /// Initializes the stable debugger value.
    /// </summary>
    /// <param name="number">The numeric field value.</param>
    /// <param name="text">The textual field value.</param>
    /// <param name="evaluationSignalPath">The path written when a cancelable evaluation starts.</param>
    internal DebuggerFixtureValue(int number, string text, string evaluationSignalPath)
    {
        Number = number;
        Text = text;
        EvaluationSignalPath = evaluationSignalPath;
    }

    /// <summary>
    /// Gets the numeric field value.
    /// </summary>
    internal int Number;

    /// <summary>
    /// Gets the textual field value.
    /// </summary>
    internal string Text;

    /// <summary>
    /// Gets the file used to prove that target-code evaluation began.
    /// </summary>
    internal string EvaluationSignalPath;

    /// <summary>
    /// Computes a stable value by executing target code for debugger evaluation tests.
    /// </summary>
    /// <returns>The numeric field incremented by one.</returns>
    internal int NextNumber() => Number + 1;

    /// <summary>
    /// Adds one debugger-supplied primitive argument to the stored number.
    /// </summary>
    /// <param name="value">The value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value.</returns>
    internal int AddForDebugger(int value) => Number + value;

    /// <summary>
    /// Selects the Int32 overload for exact debugger argument binding.
    /// </summary>
    /// <param name="value">The Int32 value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value.</returns>
    internal int AddOverloadedForDebugger(int value) => Number + value;

    /// <summary>
    /// Selects the Int64 overload for exact debugger argument binding.
    /// </summary>
    /// <param name="value">The Int64 value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value and an overload marker.</returns>
    internal long AddOverloadedForDebugger(long value) => Number + value + 100;

    /// <summary>
    /// Returns the length of a debugger-supplied string argument.
    /// </summary>
    /// <param name="value">The string supplied by managed function evaluation.</param>
    /// <returns>The supplied string length.</returns>
    internal int LengthForDebugger(string value) => value.Length + Number - 42;

    /// <summary>
    /// Returns the combined length of two debugger-materialized strings.
    /// </summary>
    /// <param name="first">The first string supplied by managed function evaluation.</param>
    /// <param name="second">The second string supplied by managed function evaluation.</param>
    /// <returns>The combined length of both strings.</returns>
    internal int CombinedLengthForDebugger(string first, string second) =>
        first.Length + second.Length + Number - 42;

    /// <summary>
    /// Tests whether a debugger-supplied reference is this receiver.
    /// </summary>
    /// <param name="value">The runtime reference supplied by managed function evaluation.</param>
    /// <returns>True when the supplied reference is this receiver.</returns>
    internal bool IsSameForDebugger(DebuggerFixtureValue? value) =>
        ReferenceEquals(this, value);

    /// <summary>
    /// Tests whether a debugger-supplied reference is null.
    /// </summary>
    /// <param name="value">The reference supplied by managed function evaluation.</param>
    /// <returns>True when the supplied reference is null.</returns>
    internal bool IsNullForDebugger(object? value)
    {
        GC.KeepAlive(Number);
        return value is null;
    }

    /// <summary>
    /// Throws a stable exception for debugger function-evaluation recovery tests.
    /// </summary>
    /// <returns>This method does not return normally.</returns>
    internal int ThrowForDebugger() =>
        throw new InvalidOperationException(Text);

    /// <summary>
    /// Runs until CoreCLR cooperatively aborts the debugger-owned evaluation.
    /// </summary>
    /// <returns>This method does not return normally.</returns>
    internal int WaitForDebuggerCancellation()
    {
        File.WriteAllText(EvaluationSignalPath, "started");
        while (true)
        {
            Thread.SpinWait(10_000);
        }
    }
}
