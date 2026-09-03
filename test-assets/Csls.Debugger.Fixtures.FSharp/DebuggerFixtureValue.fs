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
