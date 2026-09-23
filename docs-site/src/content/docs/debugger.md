---
title: .NET debugger
description: Run and integrate the csls .NET debug adapter.
---

csls includes an editor-independent Debug Adapter Protocol (DAP) host for .NET
programs. The adapter supports C#, Visual Basic, F#, and other managed
languages that emit CLR metadata and Portable PDBs.

Use the task-focused guides for [setup and lifecycle](../debugger-setup/),
[breakpoints and stepping](../debugger-breakpoints/),
[evaluation and inspection](../debugger-evaluation/),
[symbols and source](../debugger-symbols/), [terminal and MCP workflows](../debugger-terminal-mcp/),
and the [compatibility and security reference](../debugger-compatibility/).

## Verify the installation

Run the platform check after installing or updating csls:

```console
csls debugger doctor
```

The check validates the packaged .NET runtime debugging shim and its required
entry points.

## Start the debug adapter

Editors and other DAP clients start the adapter with:

```console
csls debugger dap
```

The command reads DAP frames from standard input and writes DAP frames to
standard output. Diagnostics are written to standard error.

A launch request supplies an absolute `program` path. Use `cwd`, `args`, and `env`
to configure its working directory, arguments, and environment. Build the target
and resolve launch profiles and test selections before starting DAP. Arguments are
passed directly as an array. The adapter owns launched processes and terminates
their process trees if its client disconnects unexpectedly.

The adapter implements the standard DAP `restart` request for launch and attach.
It accepts the client's latest nested launch or attach arguments, retains logical
breakpoints, invalidates runtime handles, and keeps stop generations monotonic.
Launch restart replaces the owned process. Attach restart detaches and reattaches
to the independently running target.

Source and function breakpoints accept `hitCondition` values of `N`, `>=N`, or
`%N` for the exact Nth hit, the Nth and every later hit, or every Nth hit.
`N` must be a positive decimal integer. Counts span every runtime binding of the
logical breakpoint and reset when the client replaces it. An invalid value is
reported as an unverified breakpoint; valid breakpoints are installed.

Exception filter conditions accept comma-separated managed exception type
names. Each name matches that exact type and its derived exception types. Plain
stage filters and conditional stage filters are additive.

Set `suppressJITOptimizations` to `true` for a launch to request unoptimized JIT
code for modules with validated Portable PDBs. The default is `false`. CoreCLR
applies the policy during module load. The `modules` response reports `isOptimized`
when the runtime can determine it and includes policy diagnostics in `symbolStatus`.

Set `enableHotReload` to `true` on a launch to receive compiler-produced
Hot Reload updates. The default is `false`. CoreCLR applies its Edit and Continue
policy during module load. The `modules`
response reports `isHotReloadEnabled`, `hotReloadGeneration`, and any bounded
failure diagnostic. Authorized MCP clients use `debug_hot_reload` with
compiler-produced C# or Visual Basic deltas.

`justMyCode` defaults to `true` for launch and attach. The first source step
classifies modules with validated symbols and unoptimized JIT policy as user
code, then enables CoreCLR JMC stepping. Modules loaded later receive the same
policy. Set the option to `false` to make symbol-bearing `DebuggerNonUserCode`
members eligible. Module inspection reports the
effective classification through `isUserCode`.

`enableStepFiltering` defaults to `true` and skips property accessors and CLR
operator methods during Step Into. `DebuggerHidden` and `DebuggerStepThrough`
also remain filtered, while `DebuggerNonUserCode` follows `justMyCode`. Set the
option to `false` to enter properties and operators.

`symbolOptions` accepts ordered local directories or anonymous HTTP(S) symbol
servers through `searchPaths`. `searchMicrosoftSymbolServer` and
`searchNuGetOrgSymbolServer` are opt-in, while `cachePath` selects an absolute
download cache directory. Each Portable PDB must match the module's CodeView
identity before use or caching. `moduleFilter.mode` supports
`loadAllButExcluded` with `excludedModules` and `loadOnlyIncluded` with
`includedModules`; case-insensitive `*` wildcards are supported. Set
`includeSymbolsNextToModules` to preserve adjacent and embedded lookup for
otherwise excluded modules. Redirects stay within the configured authority and
preserve HTTPS transport. Responses are bounded, and store failures appear in
module diagnostics.

For a statement with multiple eligible local managed calls, `stepInTargets`
returns one generation-bound target per occurrence. Passing a target to `stepIn`
enters that exact occurrence, including when the same method is called more than
once on the line. Eligible calls have a managed implementation in the same module
and a matching Portable PDB.

Go to Line uses `gotoTargets` to find destinations in the active managed method
and `goto` to move execution to a selected destination. Each location requires
approval from CoreCLR through `ICorDebugILFrame.CanSetIP`. csls repeats that check
immediately before the move. A successful `goto` response precedes the resulting
`stopped` event. Step and goto target identifiers expire when the stop generation changes.

Managed arrays expose an opaque `memoryReference` while their owning stop is
active. `readMemory` accepts signed offsets and reads at most 1 MiB per request;
the response uses the DAP-required hexadecimal address and base64 data. Resuming
execution retires every memory reference.

Managed stack frames also expose opaque `instructionPointerReference` values.
`disassemble` returns exact-count ECMA-335 instruction windows with encoded
bytes, branch labels, optional metadata names, and Portable PDB source mappings.
Out-of-range entries are explicit `invalid` placeholders, and references expire
when the target resumes. Managed IL inspection works across supported .NET
languages and target architectures.

Managed-IL instruction breakpoints accept an opaque frame reference with a signed
byte offset or a virtual address returned by `disassemble`. Each requested address
must belong to the current stop and land on an exact ECMA-335 instruction boundary.
CoreCLR binding failures mark the affected item as an unverified breakpoint with
its runtime diagnostic. Valid instruction breakpoints
rebind across module reloads and accept the same hit-count forms as source and
function breakpoints.

## Security and process ownership

- Target commands are executed directly with an argument array.
- The runtime shim is loaded from the installed csls package.
- Remote and container debugging runs the adapter inside the target environment
  and transports DAP over the editor's existing connection.
- A launched process is terminated when its owning client disappears. An
  attached process is detached and allowed to continue.

## MCP integration

The installed `csls-mcp` package uses its bundled debugger worker for these tools:

- `debug_session_start` launches an absolute managed program. An optional paired
  `initialSourcePath` and one-based `initialLine` sets a source breakpoint before
  launch.
- `debug_session_attach` attaches to one explicit positive `processId` and pauses
  by default.
- `debug_sessions_list` and `debug_session_get` read connection-owned session
  state.
- `debug_session_restart` replaces a stopped launch target or reattaches an
  attached target while preserving its `debugSession` and breakpoints. It requires
  an active agent-control grant and the exact current `stopGeneration`.
- `debug_agent_control_set` separately grants or revokes target-changing authority.
  Grants require an explicit duration from 1 through 3,600 seconds, are scoped to
  the exact MCP connection and session, expire using monotonic elapsed time, and
  are rechecked when each serialized mutation begins. Every session starts
  observation-only.
- `debug_session_end` terminates launched targets and detaches attached targets.
  Terminating an attached target additionally requires both
  `terminateAttachedTarget: true` and an active agent-control grant.
- `debug_threads_get`, `debug_stack_get`, `debug_scopes_get`,
  `debug_variables_get`, `debug_evaluate`, and `debug_watches_get` inspect one
  exact stopped generation. Watch sets return a value or an error for each
  expression. Application execution retires frame and variable handles.
  Unchanged physical frames retain their logical identifiers across debugger-owned evaluation.
- `debug_modules_get` returns a bounded managed-module page and validated symbol
  status.
- `debug_breakpoints_get` provides read-only access to every authoritative source,
  function, managed-IL, and managed-exception breakpoint. Valid hit-count
  predicates are returned in normalized form.
- `debug_execution_control` pauses, continues, or source-steps. It requires the
  session's active agent-control grant; continue and step also require the exact current
  `stopGeneration`, and step selects a managed thread and `into`, `over`, or
  `out`. Step Into can select a `targetId` returned by
  `debug_step_targets_get`.
- `debug_source_breakpoints_set`, `debug_function_breakpoints_set`,
  `debug_instruction_breakpoints_set`, and `debug_exception_breakpoints_set`
  replace complete breakpoint sets at an exact stopped generation. They require
  an active agent-control grant; an empty list clears the corresponding set.
- `debug_exception_get`, `debug_step_targets_get`, and
  `debug_goto_targets_get` inspect exception and runtime-approved execution
  targets. Use a returned destination and an active agent-control grant with
  `debug_goto` to move a thread to that location.
- `debug_source_get`, `debug_memory_read`, and `debug_disassemble` return bounded
  source pages, target memory, and symbolic managed IL from opaque stopped-state
  references.
- `debug_output_get` pages retained stdout and stderr after a stable sequence
  cursor and reports any gap caused by bounded retention.

Clients can also read the same selected state through `csls://debug/` resource
templates for session state, output, breakpoints, threads, stacks, scopes,
variables, watches, modules, exceptions, source, memory, and managed-IL
disassembly. Every stopped-state URI carries the exact `stopGeneration`, and
bounded collections expose their cursor or paging inputs in the URI template.

MCP clients can subscribe to their connection's debugger resources through
`subscriptions/listen`. csls streams updates from engine state, output, and
breakpoint-binding events. Each update identifies its subscription.

The `diagnose_dotnet_debugger_failure`, `plan_dotnet_breakpoints`, and
`explain_dotnet_debugger_state` prompts use explicit live-session identity and
read-first evidence. `triage_dotnet_dump` inspects an open dump session's snapshot,
managed threads, stacks, and modules. These prompts gather evidence through read-only
operations.

Each lifecycle result returns an opaque `debugSession` identifier and current
`stopGeneration`. Later operations select the debugger target with that identity.

Stack, variable, module, source, memory, and disassembly results are bounded;
source pages include `nextStart` until complete. The newest 1,024 target-output
segments are retained, with each segment bounded to 8,192 characters. Expected
failures are MCP errors with stable codes in `_meta.errorCode`, including
`debugger_control_denied`, `debugger_invalid_state`,
`debugger_request_invalid`, and `debugger_stale_generation`. Successful calls
return structured content plus a matching JSON text representation.

The MCP process supervises one isolated debugger worker per target through
inherited standard-stream handles. MCP disconnect
terminates launched process trees and detaches attached processes before the
workers exit.

## Client behavior

Clients discover supported capabilities through `initialize`. Use scope, variable,
and memory handles with the stop generation that returned them.
Logical frame identifiers survive debugger-owned evaluation when the same physical
frame remains stopped. csls reacquires its native binding before inspection.
Resuming or stepping the application retires those frame identifiers. Use the
current stop generation for MCP stopped-state requests, including after evaluation.
