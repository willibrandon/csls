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
        Pair = (number, text);
        EvaluationSignalPath = evaluationSignalPath;
    }

    /// <summary>
    /// Gets the numeric field value.
    /// </summary>
    public int Number;

    /// <summary>
    /// Gets the textual field value.
    /// </summary>
    public string Text;

    /// <summary>
    /// Gets the named tuple field used for declared-field metadata tests.
    /// </summary>
    public readonly (int Code, string Label) Pair;

    /// <summary>
    /// Gets the file used to prove that target-code evaluation began.
    /// </summary>
    public readonly string EvaluationSignalPath;

    /// <summary>
    /// Gets the numeric field through a side-effect-free expression-bodied getter.
    /// </summary>
    public int NumberProperty => Number;

    /// <summary>
    /// Gets the numeric field through the compiler's debug return-local pattern.
    /// </summary>
    public int BlockNumberProperty
    {
        get
        {
            return Number;
        }
    }

    /// <summary>
    /// Gets the existing string reference through a side-effect-free getter.
    /// </summary>
    public string TextProperty => Text;

    /// <summary>
    /// Tests whether the getter result retains the exact original string object.
    /// </summary>
    /// <param name="value">The string supplied through the debugger's getter expression.</param>
    /// <returns>True when the supplied reference is the original string field.</returns>
    public bool IsOriginalText(string value) => ReferenceEquals(Text, value);

    /// <summary>
    /// Gets the tuple field through its declared element names.
    /// </summary>
    public (int Code, string Label) PairProperty => Pair;

    /// <summary>
    /// Gets the tuple through element names that differ from its backing field.
    /// </summary>
    public (int Number, string Text) RenamedPairProperty => Pair;

    /// <summary>
    /// Gets the tuple through an unnamed declaration.
    /// </summary>
    public (int, string) UnnamedPairProperty => Pair;

    /// <summary>
    /// Increments the numeric field before returning its changed value.
    /// </summary>
    public int MutatingNumberProperty => ++Number;

    /// <summary>
    /// Returns this object after recording one getter execution in its numeric field.
    /// </summary>
    public DebuggerFixtureValue MutatingSelfProperty
    {
        get
        {
            Number++;
            return this;
        }
    }

    /// <summary>
    /// Computes a tuple with property-specific names through target execution.
    /// </summary>
    public (int Next, string Caption) ComputedPairProperty => (Number + 1, Text);

    /// <summary>
    /// Throws from a property getter to verify supervised evaluation recovery.
    /// </summary>
    public int ThrowingNumberProperty => ThrowForDebugger();

    /// <summary>
    /// Signals property execution and waits for cooperative debugger cancellation.
    /// </summary>
    public int CancelableNumberProperty => WaitForDebuggerCancellation();

    /// <summary>
    /// Returns the original numeric value and mutates its field in a finally handler.
    /// </summary>
    public int FinallyNumberProperty
    {
        get
        {
            try
            {
                return Number;
            }
            finally
            {
                Number++;
            }
        }
    }

    /// <summary>
    /// Replaces the numeric field through ordinary target code when a fixture requires it.
    /// </summary>
    /// <param name="value">The replacement numeric value.</param>
    internal void SetNumber(int value) => Number = value;

    /// <summary>
    /// Replaces the textual field through ordinary target code when a fixture requires it.
    /// </summary>
    /// <param name="value">The replacement textual value.</param>
    internal void SetText(string value) => Text = value;

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

    /// <summary>
    /// Exposes cooperative evaluation abort through files before completing the target's finally handler.
    /// </summary>
    /// <returns>This method exits through the debugger's cooperative abort.</returns>
    internal int WaitForDebuggerAbortRelease()
    {
        try
        {
            return WaitForDebuggerCancellation();
        }
        finally
        {
            File.WriteAllText(EvaluationSignalPath + ".aborting", "aborting");
            while (!File.Exists(EvaluationSignalPath + ".release"))
            {
                Thread.SpinWait(10_000);
            }
        }
    }

    /// <summary>
    /// Returns after the debugger test releases an evaluation through a real file boundary.
    /// </summary>
    /// <returns>The retained numeric value.</returns>
    internal int WaitForDebuggerRelease()
    {
        File.WriteAllText(EvaluationSignalPath, "started");
        while (!File.Exists(EvaluationSignalPath + ".release"))
        {
            Thread.SpinWait(10_000);
        }

        return Number;
    }
}
