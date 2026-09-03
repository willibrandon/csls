---
title: .NET debugger
description: Run and integrate the csls .NET debug adapter.
---

csls includes an editor-independent Debug Adapter Protocol (DAP) host for .NET
programs. The adapter is designed for C#, Visual Basic, F#, and other managed
languages that emit CLR metadata and Portable PDBs.

## Verify the installation

Run the platform check after installing or updating csls:

```console
csls debugger doctor
```

The check validates the packaged .NET runtime debugging shim and its required
entry points. csls does not download a debugger when a session starts.

## Start the debug adapter

Editors and other DAP clients start the adapter with:

```console
csls debugger dap
```

The command reads DAP frames from standard input and writes DAP frames to
standard output. Do not wrap it in a command that writes banners or shell output
to standard output. Diagnostics are written to standard error.

A launch request names a concrete `program`, `cwd`, argument array, and
environment map. Resolve projects, launch profiles, and test selections before
starting DAP; the adapter does not execute build commands or interpret shell
text. The adapter owns launched processes and terminates their process trees if
its client disconnects unexpectedly.

Source and function breakpoints accept `hitCondition` values of `N`, `>=N`, or
`%N` for the exact Nth hit, the Nth and every later hit, or every Nth hit.
`N` must be a positive decimal integer. Counts span every runtime binding of the
logical breakpoint and reset when the client replaces it. An invalid value is
reported as an unverified breakpoint without rejecting other breakpoints.

Exception filter conditions accept comma-separated managed exception type
names. Each name matches that exact type and its derived exception types. Plain
stage filters and conditional stage filters are additive.

Set `suppressJITOptimizations` to `true` for a launch to request unoptimized JIT
code for modules with validated Portable PDBs. The default is `false`, and the
setting does not apply to attach because CoreCLR permits the change only while a
module is loading. The `modules` response reports `isOptimized` when the runtime
can determine it and appends a diagnostic to `symbolStatus` when a request could
not be honored.

## Security and process ownership

- Target commands are executed directly without a command shell.
- The runtime shim is loaded only from the packaged application layout.
- The adapter does not open a TCP listener or record telemetry.
- Remote and container debugging runs the adapter inside the target environment
  and transports DAP over the editor's existing connection.
- A launched process is terminated when its owning client disappears. An
  attached process is detached and allowed to continue.

## MCP integration

Debugger MCP operations use the debugger's private local control protocol, not
DAP. A debugger target is always selected by an explicit `debug_session`; a
stopped-state request also supplies its `stopGeneration`. Language-workspace or
active-editor selection never implicitly chooses a process.

Read access and execution control are separate grants. Resume, stepping,
termination, assignment, function evaluation, and Hot Reload require explicit
per-session agent-control authorization. Results are bounded and cursor-paged,
and session-owned targets are cleaned up when their MCP owner disconnects.

## Client behavior

Clients should use only capabilities returned by `initialize`. Unknown or
unavailable operations return an unsuccessful protocol response. Handles for
frames, scopes, and variables are valid only for the stop generation in which
the adapter returned them.
