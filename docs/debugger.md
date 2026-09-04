# Debugging .NET applications

The csls debugger is an editor-independent CoreCLR debugger. VS Code, Zed, and
the terminal interface share the same runtime engine, managed-symbol reader,
breakpoint behavior, stepping rules, stack model, and value formatter. The
engine is language-neutral: C#, Visual Basic, F#, and other .NET languages that
emit Portable PDBs can use the managed source-debugging workflow on every
supported platform. Matching Windows PDBs receive the same workflow on Windows.

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
the current stop generation. A bounded auxiliary pane cycles through retained target
output, managed modules with symbol policy, authoritative breakpoints, and the current
managed exception. Watches evaluate without target-code execution in the selected frame.
F1 opens a searchable command palette for watch, execution, restart, terminate, and
detach operations.

The execution keys are:

| Key | Operation |
| --- | --- |
| F1 | Open the debugger command palette |
| F2 | Cycle output, modules, breakpoints, watches, and exception views |
| F5 | Continue |
| Shift+F5 | Terminate the target |
| F6 | Pause |
| F9 | Toggle a source breakpoint at the source cursor |
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

The adapter advertises the standard DAP `restart` request for launch and attach.
Restart accepts the client's latest nested launch or attach arguments, retains
logical breakpoint policy, invalidates runtime handles, and never reuses a stop
generation. A launch restart replaces and owns a new process; an attach restart
detaches and reattaches without terminating the independently owned target.

Source, function, and managed-IL instruction breakpoints accept source-language
`condition` expressions and DAP `hitCondition` values in the forms `N`, `>=N`,
and `%N`, where `N` is a positive decimal integer. These forms stop on exactly
the Nth matching hit, on the Nth and every later matching hit, or on every Nth
matching hit, respectively. The condition is evaluated first in the callback
thread's top managed frame using the language recorded in its PDB; only a true
condition advances the logical hit count. Hit counts span all runtime module
bindings and reset when the client replaces that breakpoint. Invalid hit
conditions produce an unverified breakpoint with a diagnostic message and do
not fail unrelated breakpoints in the same request. A condition evaluation error
is written to the debugger console and exposes the runtime stop so the expression
can be corrected instead of being silently ignored.

A non-empty source-breakpoint `logMessage` makes the breakpoint a logpoint. Text
inside `{}` is evaluated with the same side-effect-free language provider,
`{{` and `}}` produce literal braces, and a trailing newline is supplied when
needed. Conditions and hit counts are applied before interpolation. A successful
logpoint and an interpolation error both continue without publishing a stopped
event; errors are written to debugger-console output.

Each advertised exception stage accepts a DAP filter condition containing one
or more comma-separated managed exception type names. A name matches both an
exact thrown type and any exception derived from that base type. Multiple plain
filters and conditional filter options are additive, and replacing the request
atomically replaces the complete exception policy.

Set `suppressJITOptimizations` to `true` on a launch request to ask CoreCLR to
disable JIT optimizations for each module with validated managed symbols. The
default is `false`. This setting is launch-only because CoreCLR accepts the
policy change only during the module-load callback, before code is compiled.
The DAP `modules` response reports `isOptimized` when CoreCLR exposes the state;
if the runtime rejects the policy or cannot report it, `symbolStatus` includes a
bounded diagnostic instead of claiming that suppression succeeded.

Set `enableHotReload` to `true` on a launch request to prepare symbol-bearing
modules for compiler-driven Hot Reload. This option is disabled by default and
is launch-only because CoreCLR accepts the required Edit and Continue policy
during module load. The DAP `modules` response reports the runtime decision as
`isHotReloadEnabled`, the committed `hotReloadGeneration`, and any bounded
failure diagnostic in `symbolStatus`.

The compiler owns language-specific edit analysis. It must reject rude edits
and supply one matched metadata, IL, and minimal Portable PDB delta generation.
When an updated method is active, it must also supply the old method token,
method version, and IL offset together with the updated zero-based source span.
The debugger validates module identity, generation ordering, payload bounds,
Portable PDB continuity, and every active-statement mapping before applying the
generation atomically. It then overlays the current document checksums, rebinds
source, function, and managed-IL instruction breakpoints, remaps active methods
to exact compiler-selected instructions, and invalidates stopped-state handles.

C# and Visual Basic compiler services provide this delta workflow. Ordinary F#
debugging remains first-class, but F# Hot Reload is not claimed until its
compiler service exposes complete delta emission and active-statement mapping.
DAP has no standard apply-update request, so `enableHotReload` prepares the
runtime while the authorized MCP workflow applies compiler updates.

Source, function, instruction, and temporary stepping breakpoints are installed
against managed IL. CoreCLR projects those breakpoints into code that is already
JIT-compiled and into code produced by later tiered compilations, so promotion
from quick Tier 0 code to optimized Tier 1 code does not make a breakpoint stale.
The `isOptimized` module field reports the module's effective JIT policy; it does
not guess a per-method optimization tier that the public ICorDebug contract does
not expose.

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
currently offers calls whose managed implementation and debug symbols are in the
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
metadata symbols, and managed-symbol source mappings. `setInstructionBreakpoints`
accepts either one of those frame references plus a signed byte offset or a virtual
address returned by `disassemble`. The address must belong to the current stop and
land on an exact IL instruction boundary.

CoreCLR can reject a valid IL boundary when the JIT cannot patch it. That item is
returned as unverified with the runtime diagnostic without rejecting the rest of
the replacement set. Successful instruction breakpoints rebind when the same module
reloads, support the same conditions and hit-count forms as source and function
breakpoints, and stop with the `instruction breakpoint` reason. Managed-IL
references expire when execution resumes; installed logical breakpoints retain
their independently validated module, method, and IL identities.

## Runtime and symbol requirements

The target must run CoreCLR and match the host architecture. Source breakpoints,
stack source locations, argument names, and local names require matching managed
symbols. Portable PDBs work on Windows, Linux, and macOS; identity-matched Windows
PDBs work on Windows through Microsoft's public DiaSymReader component. Immediate
primitive, string, field, object, and array inspection does not execute target
code. Handles for frames, scopes, and variables are valid only for the stop at
which they were returned.

DAP `evaluate` selects expression syntax from the selected frame's PDB language
identity. C#, Visual Basic, and F# expressions are parsed in a lazy per-session
managed compiler worker, lowered to a bounded versioned IR, and bound to
current-generation runtime values in the debugger engine. Locals, arguments,
`this`/`Me`, literals, instance fields, array indexes, built-in unary and binary
operators, explicit built-in primitive conversions, short-circuit Boolean operations,
and conditional expressions do not execute target code. C# casts, Visual Basic
`CType` and predefined conversions, and F# numeric conversion functions lower to
the same language-neutral conversion operation. Unknown Portable-PDB languages use
the portable CLR subset for locals, arguments, fields, and array indexes.

DAP expression completions are derived from the exact selected stop rather than an
unrelated editor workspace. Root completion includes current-generation locals,
arguments, and language-appropriate literal keywords. Member completion evaluates
only a side-effect-free receiver and walks its exact CoreCLR type hierarchy and loaded
module metadata for fields and methods the evaluator currently supports. Explicitly
qualified loaded types provide static-method completion. Results are prefix-filtered,
bounded to 2,048 candidates, carry UTF-16 replacement spans, and use case-insensitive
matching for Visual Basic. If `frameId` is omitted, the selected stopped thread's top
managed frame supplies the context.

Object expansion follows the value's exact CoreCLR type and base-type chain rather
than assuming every declaring type belongs to the object's leaf module. Inherited
fields therefore expand across loaded assemblies in one deterministic paged sequence,
and retain the same evaluate names used by completion, evaluation, and assignment.
Immediate value presentation uses the exact CoreCLR type plus the declaring module's
metadata. Generic types, arrays, tuples, and nullable value types therefore retain
their concrete type arguments; arrays include their live dimensions and nullable
values display either their contained value or `null` without target execution.
Enums use their actual underlying runtime storage and metadata constants, including
symbolic decomposition for `[Flags]`; unnamed values retain their numeric display.
`decimal` values are reconstructed from their 96-bit integer, scale, and sign fields
inside the debugger process, preserving significant trailing zeroes without invoking
formatting code in the debuggee.
`DateTime`, `DateTimeOffset`, `TimeSpan`, and `Guid` values are likewise reconstructed
from validated runtime storage and rendered with deterministic invariant forms. Local
`DateTime` values are labeled without borrowing the debugger host's time-zone offset.
String and character displays escape quotes, backslashes, NUL, standard control
characters, other control code points, and unpaired UTF-16 surrogates deterministically
while preserving valid Unicode scalar pairs, so protocol payloads never depend on an
editor's handling of embedded control text.

The frame is explicit when supplied; otherwise the adapter uses the selected
stopped thread's top managed frame. The same generation-bound read-only operation
is available through private `debugger/evaluate` RPC and the MCP `debug_evaluate`
tool. Both reject every expression that could execute target code. DAP `evaluate`
and the separate private `debugger/executeExpression` operation additionally accept
explicitly qualified instance-method and loaded-type static-method calls in C#,
Visual Basic, and F# when CoreCLR permits function evaluation at the selected frame.
Calls accept up to 64 arguments that bind to exact CLR primitive values, null,
retained runtime object and array
references, or literal and side-effect-free computed strings. String arguments are
allocated inside the target with their exact length, including embedded NUL characters.
Method binding follows the exact runtime type and its inherited base types, including
methods implemented in another loaded assembly. Optimized methods,
prologs, native frames, GC-unsafe points, and other runtime-restricted locations
return the CoreCLR failure instead of attempting a less safe evaluation. Static calls
resolve an explicitly qualified metadata type from the target's loaded managed modules;
missing and ambiguous type identities fail before target execution.

Function evaluation runs only the selected managed thread while the other managed
threads remain stopped. One call may run at a time and has a five-second deadline.
DAP advertises request cancellation; cancellation and deadline expiry use
`ICorDebugEval.Abort`, wait for CoreCLR's completion callback, and never escalate
to `RudeAbort`. A call result, thrown exception, or cooperative abort invalidates
the client's stack and variable handles because target code may allocate, collect,
or mutate state. If cooperative abort cannot restore a trustworthy stop, the
session faults and must be disconnected. Explicit C# `new T(...)`, Visual Basic
`New T(...)`, and F# `new T(...)` expressions use the guarded path for loaded
non-generic runtime types and bind the constructor by metadata signature. Generic
construction and object or collection initializers remain rejected until their full
binding and materialization semantics are available. Unsupported value-type arguments,
overload sets that exact metadata parameter identities cannot select uniquely,
assignments, properties, user-defined operators, and implicit `ToString` execution are
also rejected. Variables include `evaluateName` only when csls can provide a valid
source expression for the value.

DAP `setVariable` and `setExpression` apply direct stopped-state writes without
executing target code. Writable targets are named locals, arguments, instance fields,
and managed array elements. The assigned value uses the selected frame's same
side-effect-free C#, Visual Basic, F#, or portable CLR evaluator. Exact primitives,
checked contextual integral literals, language-valid built-in numeric widening,
explicit built-in primitive conversions, null, and existing runtime references of the
same displayed type are supported. Direct writes preserve the stop generation because
they cannot run target code or trigger collection, and DAP publishes variable
invalidation so aliased views refresh. Constructing a replacement object or string,
boxing, reference conversions, and user-defined conversions remain unsupported until
they can use the guarded function-evaluation lifecycle.

Assemblies loaded from PE and Portable PDB byte arrays receive the same source
breakpoint, stack, local-name, stepping, goto, disassembly, and managed-IL
breakpoint behavior as file-backed assemblies. During launch, csls consumes the
runtime's symbol-update stream before continuing the callback. During attach, it
recovers the current immutable snapshot through `ICorDebugModule3`; dynamic
module snapshots are refreshed when CoreCLR reports newly loaded classes. No
temporary PE or PDB file is required.

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
Microsoft and NuGet.org servers remain opt-in. Downloaded managed PDBs are
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
  before launch. `sourceFileMap` maps absolute build-machine prefixes from PDBs
  to absolute local source prefixes; POSIX, drive-letter, and UNC paths are
  accepted on every host. Set `suppressJitOptimizations` when reliable target-code
  evaluation is more important than optimized target execution.
- `debug_session_attach` attaches to one positive operating-system `processId`
  and pauses by default. It accepts the same `sourceFileMap` when the attached
  target was built in another location.
- `debug_sessions_list` lists only sessions owned by the current MCP connection.
- `debug_session_get` reads one session selected by its returned `debugSession`.
- `debug_agent_control_set` is the separate authorization boundary for all
  target-changing operations after activation. Enabling control requires an
  explicit duration from 1 through 3,600 seconds. The grant belongs only to the
  current MCP connection and `debugSession`, expires against a monotonic clock,
  can be revoked immediately, and is never inherited by another session or
  connection. Session results expose `agentControl` and include
  `agentControlExpiresAtUtc` while a grant is active; observation never creates
  or renews a grant.
- `debug_session_restart` replaces a stopped launch target or reattaches an attached
  target while retaining `debugSession` and breakpoint policy. It requires
  an active agent-control grant and the exact current `stopGeneration`.
- `debug_session_end` terminates a launched target. It safely detaches an attached
  target unless `terminateAttachedTarget` and the session's explicit
  agent-control grant are both active.
- `debug_threads_get`, `debug_stack_get`, `debug_scopes_get`, and
  `debug_variables_get` inspect one exact stopped generation. Frame and variable
  handles expire as soon as execution resumes.
- `debug_evaluate` evaluates the same source-language-aware, side-effect-free
  expression subset as DAP in an explicit current-generation frame. It is
  read-only and does not require an agent-control grant. `debug_watches_get`
  evaluates up to 64 watches in order and preserves an independent stable error
  for each failed expression so one invalid watch does not hide valid values.
- `debug_execute_expression` executes an explicitly qualified instance or loaded-type
  static method with supported bounded arguments in an explicit current-generation
  frame. It requires
  an active agent-control grant and the exact `stopGeneration`; successful, exceptional,
  and cooperatively cancelled execution invalidates old frame and variable handles
  and advances the stop generation. The tool is marked destructive, non-idempotent,
  and open-world because the target method may mutate local or external state.
- `debug_variable_set` and `debug_expression_set` directly assign a local, argument,
  instance field, or managed array element using a side-effect-free value expression.
  Both require an active agent-control grant and the exact `stopGeneration`, return the updated
  value at that unchanged generation, and publish variable-resource invalidation.
- `debug_modules_get` returns a bounded module page and validated symbol status.
- `debug_breakpoints_get` reads every authoritative source, function, managed-IL,
  and managed-exception breakpoint without granting target control. Conditions
  and log messages are preserved, and valid hit-count predicates are returned in
  normalized form.
- `debug_execution_control` pauses, continues, or source-steps a session.
  Execution control requires an active agent-control grant; continue and step also require
  the exact current `stopGeneration`, and step selects one managed thread and
  `into`, `over`, or `out` behavior. Step Into can also select a `targetId`
  returned by `debug_step_targets_get`.
- `debug_source_breakpoints_set`, `debug_function_breakpoints_set`,
  `debug_instruction_breakpoints_set`, and `debug_exception_breakpoints_set`
  replace their complete breakpoint set. They require an active agent-control grant, an
  exact stopped generation, and accept an empty list to clear the set. Source,
  function, and managed-IL requests accept source-language conditions; source
  requests also accept interpolated log messages.
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
- `csls://debug/breakpoints/{debugSession}`
- `csls://debug/threads/{debugSession}/{stopGeneration}`
- `csls://debug/stack/{debugSession}/{stopGeneration}/{threadId}{?startFrame,levels}`
- `csls://debug/scopes/{debugSession}/{stopGeneration}/{frameId}`
- `csls://debug/variables/{debugSession}/{stopGeneration}/{variablesReference}{?start,count}`
- `csls://debug/watches/{debugSession}/{stopGeneration}/{frameId}{?expression}`
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
events from engine state, output, and breakpoint-binding notifications. Agent-control
grant, revoke, and expiry transitions also publish the exact session resource so
subscribed clients do not cache stale authorization state. The path is event-driven
and does not poll the debug target. Legacy resource subscription RPCs are not exposed.

Four read-first prompts are advertised with the debugger worker:
`diagnose_dotnet_debugger_failure`, `plan_dotnet_breakpoints`, and
`explain_dotnet_debugger_state` for live debugging, plus `triage_dotnet_dump` for
an already-opened read-only dump session. They require an explicit `debugSession`,
ground their guidance in bounded debugger evidence, and explicitly prohibit hidden
execution or breakpoint mutations. Dump triage is limited to the session snapshot,
managed threads, managed stacks, and modules that dump inspection currently exposes.

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
