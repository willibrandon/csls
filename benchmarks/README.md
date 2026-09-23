# Performance measurement

`Csls.Benchmarks` contains focused BenchmarkDotNet microbenchmarks for code paths
that need isolated measurement, including real LSP workers and AF_UNIX control
sessions. `Csls.EndToEndPerformance` measures the published Native AOT tools through
their real LSP, MCP, CLI, control socket, and Hex1b dashboard.

Run selected microbenchmarks with generated disassembly from the repository root:

```console
dotnet run --file scripts/Run-Benchmarks.cs -- --filter "*DocumentUri*" --disassembly
```

Run the complete end-to-end measurement from the repository root:

```console
dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- --runtime linux-x64
dotnet run --file scripts/Run-EndToEndPerformance.cs
```

The runner measures this repository by default and writes a versioned JSON report
under `artifacts/end-to-end-performance`. Pass `--workspace` to measure another real
solution and `--iterations` to collect a cold process followed by warm processes.
Every iteration measures startup, workspace load, language operations, analyzers and
generators, MCP, dashboard attachment, shutdown, and a transient CLI query. The JSON
report includes operation timings, process-tree memory and CPU, machine details, SDK
version, probe document, analyzer assemblies, command names, and cache state.

## Debugger measurements

Measure a published csls installation from the repository root:

```console
dotnet run --project benchmarks/Csls.EndToEndPerformance --configuration Release -- debugger --server /absolute/path/to/csls --fixture-source benchmarks/Csls.EndToEndPerformance/DebuggerPerformanceTarget.cs --iterations 3 --samples 5
```

Each fresh debugger process launches the compiled harness target, binds its source
breakpoint, inspects threads, a stack frame, locals and an array page, evaluates a
watch expression, and receives target output and exit. Every operation validates
the observed values. Timings include request serialization, transport, response
parsing, and validation. Launch-to-breakpoint includes source-breakpoint setup.

The JSON report separates first and repeated stopped-state requests and labels
each fresh process by iteration. It records executable hashes, host and SDK details,
resident memory, process-tree CPU time, and Windows/Linux private memory. Resource
snapshots include the target and evaluator processes. Use `--operation-budget-ms`
to gate each operation's median, `--timeout-seconds` to bound each session, and
`--output` to choose a new report path. Completed budget failures retain their
measurements and return exit code 1.
