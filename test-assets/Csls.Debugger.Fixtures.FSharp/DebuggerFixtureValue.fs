namespace Csls.Debugger.Fixtures.FSharp

/// <summary>
/// Provides a stable F# receiver for debugger function-evaluation tests.
/// </summary>
type internal DebuggerFixtureValue(number: int) =
    /// <summary>
    /// Gets the value returned by the debugger-visible method.
    /// </summary>
    member internal _.Number = number

    /// <summary>
    /// Computes a stable result by executing target code.
    /// </summary>
    /// <returns>The stored number incremented by one.</returns>
    member internal _.NextNumber() = number + 1

    /// <summary>
    /// Adds one debugger-supplied argument to the stored number.
    /// </summary>
    /// <param name="value">The value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value.</returns>
    member internal _.AddNumber(value: int) = number + value
