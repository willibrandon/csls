---
title: Performance
description: Measure csls microbenchmarks, startup, workspace load, memory, and size.
---

Correct language results and safe edits come first. Performance work follows with a
measured workload and a regression check that preserves those results.

## Microbenchmarks

`Csls.Benchmarks` uses BenchmarkDotNet for in-process code paths such as protocol
serialization, scheduling, completion, diagnostics, formatting, Razor mapping,
workspace discovery, and edit planning.

```console
dotnet run --file scripts/Run-Benchmarks.cs -- --job Dry
dotnet run --file scripts/Run-Benchmarks.cs -- --filter "*Completion*"
dotnet run --file scripts/Run-Benchmarks.cs
```

The dry job validates benchmark construction in pull requests. Scheduled and release
runs use the full BenchmarkDotNet jobs and retain machine-readable reports.

## End-to-end measurements

`Csls.EndToEndPerformance` runs outside the measured server. It publishes the real
Native AOT launcher, starts a fresh process, speaks production LSP, loads a real
solution, waits for the Roslyn workspace to report ready, samples the complete
process tree, and performs a clean shutdown.

```console
dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- --runtime linux-x64
dotnet run --file scripts/Run-EndToEndPerformance.cs -- --iterations 3
```

The first protocol response ends startup measurement. The `initialize` request
begins workspace-load measurement, which ends when the workspace contains projects
and source documents in the ready phase. The report records startup, workspace load,
total ready time, project count, document count, process count, working set, and
private memory for every iteration.

Results are written to `artifacts/end-to-end-performance/results.json`. The first
iteration is marked cold and later fresh processes are marked warm. CI runs the
measurement on Windows, Linux, and macOS with broad hosted-runner budgets. Scheduled
runs use more iterations so trends can be compared without turning ordinary runner
noise into a pull-request failure.

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
