using System.Diagnostics;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Provides a stable C# receiver for debugger function-evaluation tests.
/// </summary>
[DebuggerDisplay("csharp={_number}", Type = "csharp-display")]
internal sealed class DebuggerFixtureValue
{
    private readonly int _number;

    /// <summary>
    /// Initializes the C# debugger receiver.
    /// </summary>
    /// <param name="number">The value returned by the debugger-visible method.</param>
    internal DebuggerFixtureValue(int number)
    {
        _number = number;
    }

    /// <summary>
    /// Gets the value returned by the debugger-visible method.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Computes a stable result by executing target code.
    /// </summary>
    /// <returns>The stored number incremented by one.</returns>
    internal int NextNumber() => Number + 1;

    /// <summary>
    /// Adds one debugger-supplied argument to the stored number.
    /// </summary>
    /// <param name="value">The value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value.</returns>
    internal int AddNumber(int value) => Number + value;

    /// <summary>
    /// Returns the length of a debugger-materialized string.
    /// </summary>
    /// <param name="value">The string supplied by managed function evaluation.</param>
    /// <returns>The supplied string length.</returns>
    internal int StringLength(string value) => value.Length + Number - 41;
}
