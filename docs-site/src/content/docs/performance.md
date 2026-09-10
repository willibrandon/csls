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
more than 10 percent slower and its measurements fall entirely outside the baseline
interquartile range.

## End-to-end measurements

`Csls.EndToEndPerformance` runs outside the measured tools. It publishes the real
Native AOT `csls` and `csls-mcp` launchers, loads a real solution, and drives the
production interfaces.

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

## Debugger measurements

Run the debugger workload from a csls source checkout, passing the absolute path
to your published csls installation:

```console
dotnet run --project benchmarks/Csls.EndToEndPerformance --configuration Release -- debugger --server /absolute/path/to/csls --fixture-source benchmarks/Csls.EndToEndPerformance/DebuggerPerformanceTarget.cs --iterations 3 --samples 5
```

Each iteration starts a fresh debugger, launches the compiled target, and stops at
its source breakpoint. The workload measures thread and stack inspection, locals,
array paging, expression evaluation, target output, and shutdown. It verifies the
source location and values as part of each measured operation.

Reports under `artifacts/end-to-end-performance` separate first and repeated
requests and include executable hashes, machine and SDK details, resident memory,
process-tree CPU time, and Windows/Linux private memory. Resource snapshots include
the debugger, target, and evaluator. Launch-to-breakpoint timing includes breakpoint
setup.

Use `--operation-budget-ms` to set the maximum median for each operation,
`--timeout-seconds` to set the session deadline, and `--output` to select a new
report path. A completed run that exceeds its budget retains the report and returns
exit code 1. Compare runs with the same binaries, build configuration, sampling
options, and machine conditions.

## Native AOT size

Each runtime package enables Native AOT compiler statistics for the `csls` and
`csls-mcp` launchers. [Dotsider](https://dotsider.dev/) compares those reports
with the stored runtime baseline and enforces the package size budget. Managed
workers are measured separately through package verification and end-to-end memory.

When changing a hot path, keep the BenchmarkDotNet report, the end-to-end JSON
report, and the Native AOT size result together. Evaluate startup time, worker
count, ready-state memory, and language behavior alongside the microbenchmark result.
