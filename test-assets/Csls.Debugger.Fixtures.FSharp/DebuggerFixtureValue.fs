namespace Csls.Debugger.Fixtures.FSharp

open System.Diagnostics

/// <summary>
/// Provides a stable F# receiver for debugger function-evaluation tests.
/// </summary>
[<DebuggerDisplay("fsharp={storedNumber}", Type = "fsharp-display")>]
type internal DebuggerFixtureValue(number: int) =
    let storedNumber = number

    /// <summary>
    /// Gets the value returned by the debugger-visible method.
    /// </summary>
    member internal _.Number = storedNumber

    /// <summary>
    /// Computes a stable result by executing target code.
    /// </summary>
    /// <returns>The stored number incremented by one.</returns>
    member internal _.NextNumber() = storedNumber + 1

    /// <summary>
    /// Adds one debugger-supplied argument to the stored number.
    /// </summary>
    /// <param name="value">The value supplied by managed function evaluation.</param>
    /// <returns>The stored number plus the supplied value.</returns>
    member internal _.AddNumber(value: int) = storedNumber + value

    /// <summary>
    /// Returns the length of a debugger-materialized string.
    /// </summary>
    /// <param name="value">The string supplied by managed function evaluation.</param>
    /// <returns>The supplied string length.</returns>
    member internal _.StringLength(value: string) = value.Length + storedNumber - 41
