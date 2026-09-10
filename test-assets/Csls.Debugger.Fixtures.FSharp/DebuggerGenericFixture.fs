namespace Csls.Debugger.Fixtures.FSharp

open System.Diagnostics

/// <summary>
/// Provides a closed generic F# value for debugger construction tests.
/// </summary>
[<DebuggerDisplay("generic={storedValue}", Type = "fsharp-generic")>]
type internal DebuggerGenericFixture<'T>(value: 'T) =
    let storedValue = value

    /// <summary>
    /// Initializes the generic F# debugger value with its default value.
    /// </summary>
    new() = DebuggerGenericFixture(Unchecked.defaultof<'T>)

    /// <summary>
    /// Gets the value retained by the constructed instance.
    /// </summary>
    member internal _.Value = storedValue
