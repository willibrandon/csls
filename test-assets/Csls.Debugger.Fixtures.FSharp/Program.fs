module internal Csls.Debugger.Fixtures.FSharp.Program

open System
open System.IO
open System.Globalization
open System.Threading

/// <summary>
/// Waits after executing a stable source statement for debugger inspection.
/// </summary>
/// <param name="arguments">The single signal-file path.</param>
/// <returns>Zero when the expected local value remains live.</returns>
[<EntryPoint>]
let main arguments =
    let mutable answer = Int32.Parse(arguments[1], CultureInfo.InvariantCulture)
    let value = DebuggerFixtureValue(answer)
    let numbers = [| answer; answer + 1 |]
    answer <- answer + 1
    Console.Write(arguments[2])
    Console.Out.Flush()
    while not (File.Exists(arguments[0])) do
        Thread.Sleep(1)

    GC.KeepAlive(value)
    answer + numbers[0] - 83
