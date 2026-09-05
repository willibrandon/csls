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
    let genericValue = DebuggerGenericFixture<int>(answer)
    let nestedGenericValue =
        DebuggerGenericFixture<System.Collections.Generic.List<int>>(
            System.Collections.Generic.List<int>())
    let arrayGenericValue = DebuggerGenericFixture<int[]>([||])
    let nullableGenericValue = DebuggerGenericFixture<Nullable<int>>(Nullable(answer))
    let numbers = [| answer; answer + 1 |]
    let pairs = [| ValueTuple<int, int>(0, 142); ValueTuple<int, int>(151, 152) |]
    answer <- answer + 1
    Console.Write(arguments[2])
    Console.Out.Flush()
    while not (File.Exists(arguments[0])) do
        Thread.Sleep(1)

    GC.KeepAlive(value)
    GC.KeepAlive(genericValue)
    GC.KeepAlive(nestedGenericValue)
    GC.KeepAlive(arrayGenericValue)
    GC.KeepAlive(nullableGenericValue)
    GC.KeepAlive(pairs)
    answer + numbers[0] - 83
