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
