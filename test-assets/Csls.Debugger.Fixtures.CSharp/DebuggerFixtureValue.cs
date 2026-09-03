namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Provides a stable C# receiver for debugger function-evaluation tests.
/// </summary>
internal sealed class DebuggerFixtureValue
{
    /// <summary>
    /// Initializes the C# debugger receiver.
    /// </summary>
    /// <param name="number">The value returned by the debugger-visible method.</param>
    internal DebuggerFixtureValue(int number)
    {
        Number = number;
    }

    /// <summary>
    /// Gets the value returned by the debugger-visible method.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Computes a stable result by executing target code.
    /// </summary>
    /// <returns>The stored number incremented by one.</returns>
    internal int NextNumber() => Number + 1;
}
