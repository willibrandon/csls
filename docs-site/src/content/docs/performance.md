---
title: Performance
description: Measure csls microbenchmarks, real product operations, resources, and size.
---

Correct language results and safe edits come first. Performance work follows with a
measured workload and a regression check that preserves those results.

## Microbenchmarks

`Csls.Benchmarks` uses BenchmarkDotNet for focused code paths such as protocol
serialization, scheduling, completion, diagnostics, formatting, Razor mapping,
workspace routing, semantic tokens, workspace symbols, and AF_UNIX control requests.

```console
dotnet run --file scripts/Run-Benchmarks.cs -- --job Dry
dotnet run --file scripts/Run-Benchmarks.cs -- --filter "*Completion*"
dotnet run --file scripts/Run-Benchmarks.cs -- --disassembly
```

Repeat `--filter` to select several benchmark groups. The dry job validates benchmark
construction in pull requests. Scheduled and release runs retain machine-readable
reports, code size, and disassembly. Pull requests also run stable base and candidate
benchmarks on the same runner. A regression fails only when the candidate median is
more than 10 percent slower and its measurements no longer overlap the baseline
interquartile range.

## End-to-end measurements

`Csls.EndToEndPerformance` runs outside the measured tools. It publishes the real
Native AOT `csls` and `csls-mcp` launchers, loads a real solution, and drives the
production interfaces instead of calling implementation classes.

```console
dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- --runtime linux-x64
dotnet run --file scripts/Run-EndToEndPerformance.cs -- --iterations 3
```

Each iteration measures process startup, LSP initialization, solution loading,
diagnostics, hover, completion, code actions, formatting, document changes,
analyzers and generators, MCP, Hex1b dashboard attachment, shutdown, and a transient
CLI query. It samples the complete language-server process tree for process count,
working set, private memory, processor time, and normalized CPU use.

Results are written to `artifacts/end-to-end-performance/results.json`. The first
iteration is marked cold and later fresh processes are marked warm. The versioned
report records the OS, CPU, memory limit, architecture, runtime, SDK, workspace,
probe document, analyzer assemblies, commands, cache state, and iteration count. CI
runs it on Windows, Linux, and macOS. Scheduled runs use three iterations.

## Native AOT size

Each runtime package enables Native AOT compiler statistics for the `csls` and
`csls-mcp` launchers. Dotsider compares those reports with the stored runtime
baseline and enforces the package size budget. Managed workers are measured through
package verification and end-to-end memory rather than being folded into the native
launcher total.

When changing a hot path, keep the BenchmarkDotNet report, the end-to-end JSON
report, and the Native AOT size result together. A faster microbenchmark does not
justify slower startup, extra worker processes, higher ready-state memory, or a
change in language behavior.
