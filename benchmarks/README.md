# Performance measurement

`Csls.Benchmarks` contains focused BenchmarkDotNet microbenchmarks for code paths
that can run in process. `Csls.EndToEndPerformance` measures the published Native
AOT launcher, managed language-server worker, Roslyn workspace load, complete
process tree, working set, and private memory.

Run the complete end-to-end measurement from the repository root:

```console
dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- --runtime linux-x64
dotnet run --file scripts/Run-EndToEndPerformance.cs
```

The runner measures this repository by default and writes a versioned JSON report
under `artifacts/end-to-end-performance`. Pass `--workspace` to measure another real
solution and `--iterations` to collect a cold process followed by warm processes.
Every iteration performs a complete LSP initialize, waits for the Roslyn workspace
to report ready with projects and documents, samples the launcher process tree, and
uses the normal shutdown handshake. Startup ends at the first protocol response;
workspace load begins with `initialize` and ends when the Roslyn workspace is ready.
