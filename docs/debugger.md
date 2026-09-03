# Debugging .NET applications

The csls debugger is an editor-independent CoreCLR debugger. VS Code, Zed, and
the terminal interface share the same runtime engine, Portable PDB reader,
breakpoint behavior, stepping rules, stack model, and value formatter. The
engine is language-neutral: C#, Visual Basic, F#, and other .NET languages that
emit Portable PDBs can use the managed source-debugging workflow.

## Terminal launch

Build the target first, then provide the concrete managed executable or assembly,
the initial source file, and a one-based executable line:

```console
csls debugger tui launch ./bin/Debug/net10.0/App.dll \
  --source ./Program.cs \
  --line 12 \
  -- argument-one argument-two
```

Use `--cwd <directory>` to choose the target working directory. Use
`--runtime <path>` only when a specific compatible `dotnet` host is required.
Arguments after `--` are passed to the target without interpretation.

The terminal opens after the initial source breakpoint stops the process. Its
source pane follows the selected managed frame; the stack pane shows managed
callers; and the variables pane reads arguments and lexically active locals from
the current stop generation.

The execution keys are:

| Key | Operation |
| --- | --- |
| F5 | Continue |
| F6 | Pause |
| F10 | Step over |
| F11 | Step into |
| F12 | Step out |
| Ctrl+C | Close the debugger |

Closing a launch session terminates the debugger-owned target process tree.

## Terminal attach

Attach to a running CoreCLR process by its operating-system process identifier:

```console
csls debugger tui attach 12345
```

The debugger pauses the process before opening the terminal. Closing an attach
session detaches and leaves the target running.

## Editor integration

The VS Code and Zed extensions invoke the standard Debug Adapter Protocol host
directly:

```console
csls debugger dap
```

DAP uses standard input and output exclusively for protocol messages. Diagnostic
text is written to standard error. A concrete `program` is required; project,
launch-profile, and test discovery belongs to the editor or calling tool.

Source and function breakpoints accept DAP `hitCondition` values in the forms
`N`, `>=N`, and `%N`, where `N` is a positive decimal integer. These forms stop
on exactly the Nth hit, on the Nth and every later hit, or on every Nth hit,
respectively. Hit counts belong to the logical breakpoint, span all of its
runtime module bindings, and reset when the client replaces that breakpoint.
Invalid hit conditions produce an unverified breakpoint with a diagnostic
message; they do not fail unrelated breakpoints in the same request.

Each advertised exception stage accepts a DAP filter condition containing one
or more comma-separated managed exception type names. A name matches both an
exact thrown type and any exception derived from that base type. Multiple plain
filters and conditional filter options are additive, and replacing the request
atomically replaces the complete exception policy.

Set `suppressJITOptimizations` to `true` on a launch request to ask CoreCLR to
disable JIT optimizations for each module with a validated Portable PDB. The
default is `false`. This setting is launch-only because CoreCLR accepts the
policy change only during the module-load callback, before code is compiled.
The DAP `modules` response reports `isOptimized` when CoreCLR exposes the state;
if the runtime rejects the policy or cannot report it, `symbolStatus` includes a
bounded diagnostic instead of claiming that suppression succeeded.

`justMyCode` defaults to `true` for launch and attach. Before the first source
step, the debugger marks symbol-bearing, unoptimized modules as user code and
enables the CoreCLR JMC stepper so framework, symbol-free, and optimized modules
are skipped. Later-loaded modules receive the same policy. Set `justMyCode` to
`false` to make `DebuggerNonUserCode` members eligible while source stepping
still avoids symbol-free code. The `modules` response reports the effective
classification as `isUserCode`; a Release module becomes user code when
successful JIT optimization suppression makes it debuggable.

`enableStepFiltering` defaults to `true` and makes Step Into skip property
accessors and CLR operator methods. Set it to `false` when debugging inside
those members. `DebuggerHidden` and `DebuggerStepThrough` remain step filters;
`DebuggerNonUserCode` is also excluded while `justMyCode` is enabled. Filtering
uses CLR metadata tokens and therefore applies consistently to C#, Visual Basic,
F#, and other managed languages rather than depending on source syntax.

When one source statement contains multiple eligible calls, clients can request
`stepInTargets` and pass the selected target identifier to `stepIn`. The debugger
currently offers calls whose managed implementation and Portable PDB are in the
same loaded module. Repeated calls to the same method remain distinct targets,
and selecting a later occurrence skips earlier occurrences on that statement.
Target identifiers expire as soon as the process reaches another stop.

Go to Line uses DAP `gotoTargets` and `goto`. A destination is offered only when
it is a visible sequence point in the active method and CoreCLR returns `S_OK`
from `ICorDebugILFrame.CanSetIP`, which is the runtime's guarantee that continued
execution is safe and correct. The debugger repeats that check immediately before
moving the instruction pointer. The successful `goto` response is sent before the
new `stopped` event, and all old frame, variable, memory, disassembly, step-target,
and goto-target handles then become stale. csls does not offer advisory targets
for which CoreCLR cannot guarantee correct continuation.

Managed stack frames expose opaque `instructionPointerReference` values.
`disassemble` returns exact-count ECMA-335 instruction windows with encoded bytes,
metadata symbols, and Portable PDB source mappings. `setInstructionBreakpoints`
accepts either one of those frame references plus a signed byte offset or a virtual
address returned by `disassemble`. The address must belong to the current stop and
land on an exact IL instruction boundary.

CoreCLR can reject a valid IL boundary when the JIT cannot patch it. That item is
returned as unverified with the runtime diagnostic without rejecting the rest of
the replacement set. Successful instruction breakpoints rebind when the same module
reloads, support the same `hitCondition` forms as source and function breakpoints,
and stop with the `instruction breakpoint` reason. Managed-IL references expire
when execution resumes; installed logical breakpoints retain their independently
validated module, method, and IL identities.

## Runtime and symbol requirements

The target must run CoreCLR and match the host architecture. Source breakpoints,
stack source locations, argument names, and local names require a matching
Portable PDB. Immediate primitive, string, field, object, and array inspection
does not execute target code. Handles for frames, scopes, and variables are valid
only for the stop at which they were returned.

The debugger validates embedded, local, mapped, and Source Link source against
the checksum recorded in the Portable PDB. Use the established `sourceFileMap`
launch or attach option when sources moved between build and debug machines:

```json
{
  "sourceFileMap": {
    "C:\\agent\\_work\\app": "/workspaces/app"
  }
}
```

Mappings understand POSIX, Windows drive-letter, and UNC build paths regardless
of the operating system running the adapter. The most specific matching prefix
wins.

Use `symbolOptions` to add trusted local directories or symbol servers. The
Microsoft and NuGet.org servers remain opt-in. Downloaded Portable PDBs are
accepted only when their CodeView identity matches the loaded module and are
written atomically to an identity-keyed cache:

```json
{
  "symbolOptions": {
    "searchPaths": ["/srv/symbols", "https://symbols.example.com/"],
    "searchMicrosoftSymbolServer": true,
    "searchNuGetOrgSymbolServer": false,
    "cachePath": "/home/me/.cache/csls/symbols",
    "moduleFilter": {
      "mode": "loadOnlyIncluded",
      "includedModules": ["MyCompany.*.dll"],
      "includeSymbolsNextToModules": true
    }
  }
}
```

`moduleFilter.mode` is either `loadAllButExcluded` (the default) with
`excludedModules`, or `loadOnlyIncluded` with `includedModules`. Patterns are
case-insensitive and may contain `*`. `includeSymbolsNextToModules` defaults to
`true`, preserving adjacent and embedded lookup for modules excluded from
configured search paths. Search URLs must be anonymous HTTP(S) base URLs without
queries or fragments. Redirects stay on the configured authority, HTTPS cannot
downgrade, responses are bounded, and unavailable servers do not abort launch.
The default cache is `%TEMP%\SymbolCache` on Windows and
`~/.dotnet/symbolcache` on Linux and macOS.

Source Link retrieval is lazy, bounded, redirect-limited, and session-cached.
Public HTTPS endpoints are enabled by default. HTTP endpoints and localhost or
private-network hosts require a specific enabled URL rule; a catch-all `*` does
not grant private-network access:

```json
{
  "sourceLinkOptions": {
    "http://127.0.0.1:8080/source/*": { "enabled": true },
    "https://untrusted.example/*": { "enabled": false }
  }
}
```

Source Link requests do not send debugger-managed credentials or cookies, and
downloaded content is rejected unless its PDB checksum matches exactly.

Run the packaged component check when startup fails:

```console
csls debugger doctor
```

Remote and container debugging runs csls inside the target environment. The
debugger does not expose a TCP listener; its terminal control channel uses an
owner-only local endpoint created for the lifetime of the session.

## MCP integration

The installed `csls-mcp` package includes the debugger worker and advertises
debugger tools only when that worker is available. A language-only development
worker does not advertise tools that it cannot run.

- `debug_session_start` launches an absolute managed program in a new isolated
  worker. `program` and `workingDirectory` are required. The optional paired
  `initialSourcePath` and one-based `initialLine` establish a source breakpoint
  before launch.
- `debug_session_attach` attaches to one positive operating-system `processId`
  and pauses by default.
- `debug_sessions_list` lists only sessions owned by the current MCP connection.
- `debug_session_get` reads one session selected by its returned `debugSession`.
- `debug_session_end` terminates a launched target. It safely detaches an attached
  target unless `terminateAttachedTarget` and the session's explicit
  `agentControl` grant are both true.
- `debug_threads_get`, `debug_stack_get`, `debug_scopes_get`, and
  `debug_variables_get` inspect one exact stopped generation. Frame and variable
  handles expire as soon as execution resumes.
- `debug_modules_get` returns a bounded module page and validated symbol status.
- `debug_execution_control` pauses, continues, or source-steps a session.
  Execution control requires `agentControl: true`; continue and step also require
  the exact current `stopGeneration`, and step selects one managed thread and
  `into`, `over`, or `out` behavior. Step Into can also select a `targetId`
  returned by `debug_step_targets_get`.
- `debug_source_breakpoints_set`, `debug_function_breakpoints_set`,
  `debug_instruction_breakpoints_set`, and `debug_exception_breakpoints_set`
  replace their complete breakpoint set. They require `agentControl: true`, an
  exact stopped generation, and accept an empty list to clear the set.
- `debug_exception_get`, `debug_step_targets_get`, and
  `debug_goto_targets_get` inspect generation-bound stop details and
  runtime-approved execution destinations. `debug_goto` moves one thread only to
  a returned destination and requires the same control grant and generation.
- `debug_source_get`, `debug_memory_read`, and `debug_disassemble` retrieve
  bounded source-text pages, up to 65,536 target bytes, and up to 256 managed-IL
  instructions from opaque generation-bound references.
- `debug_output_get` reads retained stdout and stderr after a stable
  `afterSequence` cursor. Responses identify retention gaps with
  `droppedBeforeStart` and advertise additional retained entries with `hasMore`.

Debugger resource templates expose the same explicit, bounded projections for
clients that compose resources into their context:

- `csls://debug/session/{debugSession}`
- `csls://debug/output/{debugSession}{?afterSequence,count}`
- `csls://debug/threads/{debugSession}/{stopGeneration}`
- `csls://debug/stack/{debugSession}/{stopGeneration}/{threadId}{?startFrame,levels}`
- `csls://debug/scopes/{debugSession}/{stopGeneration}/{frameId}`
- `csls://debug/variables/{debugSession}/{stopGeneration}/{variablesReference}{?start,count}`
- `csls://debug/modules/{debugSession}{?startModule,moduleCount}`
- `csls://debug/exception/{debugSession}/{stopGeneration}/{threadId}`
- `csls://debug/source/{debugSession}/{stopGeneration}/{sourceReference}{?start,count}`
- `csls://debug/memory/{debugSession}/{stopGeneration}{?memoryReference,offset,count}`
- `csls://debug/disassembly/{debugSession}/{stopGeneration}{?instructionReference,byteOffset,instructionOffset,instructionCount,resolveSymbols}`

Stopped-state resources require the exact current generation just like their tool
counterparts, so a URI cannot silently resolve handles against a later stop.
Clients using the current MCP protocol can include exact debugger URIs in a
`subscriptions/listen` request. csls acknowledges only resources owned by that MCP
connection, then streams subscription-tagged `notifications/resources/updated`
events from engine state and output notifications. The path is event-driven and
does not poll the debug target. Legacy resource subscription RPCs are not exposed.

Three read-first prompts are advertised with the debugger worker:
`diagnose_dotnet_debugger_failure`, `plan_dotnet_breakpoints`, and
`explain_dotnet_debugger_state`. They require an explicit `debugSession`, ground
their guidance in bounded debugger evidence, and explicitly prohibit hidden
execution or breakpoint mutations.

Stack, variable, and module pages accept only non-negative offsets and at most
256 entries per call. Source results include `nextStart` until their complete
text has been read. Debugger control retains the newest 1,024 output segments,
bounds each segment to 8,192 characters, and marks a segment when older text was
truncated. Expected failures are MCP errors with stable codes in
`_meta.errorCode`, including `debugger_control_denied`,
`debugger_invalid_state`, `debugger_request_invalid`, and
`debugger_stale_generation`. Successful calls return structured content and a
matching JSON text representation.

Debugger RPC uses inherited standard-stream handles between the MCP process and
each worker. It does not open a socket or translate through DAP. Closing the MCP
connection terminates its launched process trees and detaches its attached
processes before releasing the workers. A language workspace, editor selection,
or visible process is never used as an implicit debugger target.
