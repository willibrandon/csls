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

`justMyCode` defaults to `true` for launch and attach. The first source step
classifies modules with validated symbols and unoptimized JIT policy as user
code, then enables CoreCLR JMC stepping. Modules loaded later receive the same
policy. Set the option to `false` to make `DebuggerNonUserCode` members eligible;
symbol-free code remains outside source stepping. Module inspection reports the
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
otherwise excluded modules. Redirects cannot change authority or downgrade
HTTPS, responses are bounded, and a failed server does not abort the debug
session.

For a statement with multiple eligible local managed calls, `stepInTargets`
returns one generation-bound target per occurrence. Passing a target to `stepIn`
enters that exact occurrence, including when the same method is called more than
once on the line. Calls without a same-module managed implementation and Portable
PDB are omitted rather than presented as selectable targets that cannot be honored.

Go to Line is available through `gotoTargets` and `goto` for visible sequence
points in the active managed method. csls offers a location only when CoreCLR's
`ICorDebugILFrame.CanSetIP` returns `S_OK`, the runtime guarantee for safe, correct
continued execution, and repeats the validation immediately before the move. A
successful `goto` response precedes the resulting `stopped` event. Step and goto
target identifiers expire whenever the stop generation changes.

Managed arrays expose an opaque `memoryReference` while their owning stop is
active. `readMemory` accepts signed offsets and reads at most 1 MiB per request;
the response uses the DAP-required hexadecimal address and base64 data. Resuming
execution retires every memory reference. Primitive and ordinary object values
do not advertise memory navigation, and `writeMemory` is not supported.

Managed stack frames also expose opaque `instructionPointerReference` values.
`disassemble` returns exact-count ECMA-335 instruction windows with encoded
bytes, branch labels, optional metadata names, and Portable PDB source mappings.
Out-of-range entries are explicit `invalid` placeholders, and references expire
when the target resumes. This is managed IL rather than architecture-specific
native machine code, so it works consistently across supported .NET languages
and target architectures.

Managed-IL instruction breakpoints accept an opaque frame reference with a signed
byte offset or a virtual address returned by `disassemble`. Each requested address
must belong to the current stop and land on an exact ECMA-335 instruction boundary.
If CoreCLR cannot patch an otherwise valid location, that item is returned as an
unverified breakpoint with its runtime diagnostic. Valid instruction breakpoints
rebind across module reloads and accept the same hit-count forms as source and
function breakpoints.

## Security and process ownership

- Target commands are executed directly without a command shell.
- The runtime shim is loaded only from the packaged application layout.
- The adapter does not open a TCP listener or record telemetry.
- Remote and container debugging runs the adapter inside the target environment
  and transports DAP over the editor's existing connection.
- A launched process is terminated when its owning client disappears. An
  attached process is detached and allowed to continue.

## MCP integration

The installed `csls-mcp` package advertises debugger lifecycle tools only when
its bundled debugger worker is available:

- `debug_session_start` launches an absolute managed program. An optional paired
  `initialSourcePath` and one-based `initialLine` sets a source breakpoint before
  launch.
- `debug_session_attach` attaches to one explicit positive `processId` and pauses
  by default.
- `debug_sessions_list` and `debug_session_get` read connection-owned session
  state.
- `debug_session_end` terminates launched targets and detaches attached targets.
  Terminating an attached target additionally requires both
  `terminateAttachedTarget: true` and the session's explicit `agentControl` grant.
- `debug_threads_get`, `debug_stack_get`, `debug_scopes_get`, and
  `debug_variables_get` inspect one exact stopped generation. Returned frame and
  variable handles expire when execution resumes.
- `debug_modules_get` returns a bounded managed-module page and validated symbol
  status.
- `debug_execution_control` pauses, continues, or source-steps. It requires the
  session's `agentControl` grant; continue and step also require the exact current
  `stopGeneration`, and step selects a managed thread and `into`, `over`, or
  `out`. Step Into can select a `targetId` returned by
  `debug_step_targets_get`.
- `debug_source_breakpoints_set`, `debug_function_breakpoints_set`,
  `debug_instruction_breakpoints_set`, and `debug_exception_breakpoints_set`
  replace complete breakpoint sets at an exact stopped generation. They require
  `agentControl`; an empty list clears the corresponding set.
- `debug_exception_get`, `debug_step_targets_get`, and
  `debug_goto_targets_get` inspect exception and runtime-approved execution
  targets. `debug_goto` moves a thread only to one returned generation-bound
  destination and requires `agentControl`.
- `debug_source_get`, `debug_memory_read`, and `debug_disassemble` return bounded
  source pages, target memory, and symbolic managed IL from opaque stopped-state
  references.

Each lifecycle result returns an opaque `debugSession` identifier and current
`stopGeneration`. Later operations use that explicit identity; a language
workspace, active editor, or visible process is never inferred as the debugger
target.

Stack, variable, module, source, memory, and disassembly results are bounded;
source pages include `nextStart` until complete. Expected
failures are MCP errors with stable codes in `_meta.errorCode`, including
`debugger_control_denied`, `debugger_invalid_state`,
`debugger_request_invalid`, and `debugger_stale_generation`. Successful calls
return structured content plus a matching JSON text representation.

The MCP process supervises one isolated debugger worker per target through
inherited standard-stream handles, not DAP or a network listener. MCP disconnect
terminates launched process trees and detaches attached processes before the
workers exit.

## Client behavior

Clients should use only capabilities returned by `initialize`. Unknown or
unavailable operations return an unsuccessful protocol response. Handles for
frames, scopes, variables, and memory are valid only for the stop generation in
which the adapter returned them.
