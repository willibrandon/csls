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
