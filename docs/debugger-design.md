# Native .NET debugger design

## Status and authority

This document is the normative design for the csls debugger. `plan.md` tracks
execution. Public protocol specifications and observed current runtime behavior
override examples from any implementation. An implementation discrepancy must be
recorded as a decision; it must not silently become compatibility behavior.

The design uses only public contracts and suitably licensed public dependencies.
Proprietary debugger binaries are neither linked nor inspected. They may be
exercised only through a normally licensed editor as an optional black-box user
experience comparison and never determine acceptance.

## Product definition

The product is a standalone managed .NET debugger whose engine is independent of
an editor and of any single .NET source language. It serves five clients:

1. Debug Adapter Protocol clients over standard input and output.
2. The csls Hex1b terminal debugger over private local RPC.
3. Coding agents through MCP tools and resources over private local RPC.
4. VS Code through its standard `coreclr` debug type.
5. Zed through its debug-adapter extension API.

Initial general-availability runtime support is live CoreCLR and read-only managed
dumps. C#, Visual Basic, and F# receive language-aware source debugging and
evaluation. Any other language that emits Portable PDBs receives line mapping,
stack, locals, arguments, fields, arrays, and primitive/member evaluation through
the generic CLR provider.

The host being NativeAOT does not imply that NativeAOT applications are debuggees.
NativeAOT debuggee, Mono, WebAssembly, mixed native/managed, and record/replay
support require separate runtime backends but must not require new client protocols.

## Process architecture

```text
VS Code / Zed ── DAP stdio ──┐
                              │
Terminal UI ─ private RPC ────┼── NativeAOT debugger host ── dbgshim / ICorDebug
                              │             │
MCP worker ── private RPC ────┘             └── managed evaluator worker
                                                         │
                                              Roslyn / FCS / ClrMD
```

`Csls.DebugAdapter` owns DAP framing, models, dispatch, request cancellation,
progress, sequencing, and capability negotiation. `Csls.Debugger` owns runtime
state, COM lifetimes, symbols, sources, breakpoints, stepping, and values.
`Csls.Debugger.Contracts` owns transport-safe evaluator and control contracts.
`Csls.Debugger.Client` provides the private StreamJsonRpc client. The managed
`Csls.Debugger.Worker` owns compiler services and dump inspection that are not
NativeAOT-safe.

`csls debugger dap` runs one session over standard input/output. An internal host
mode registers a discoverable local session and exposes an owner-only Unix-domain
socket or named pipe. It never listens on TCP. Remote development runs the adapter
inside the target environment and transports DAP through the editor connection.

The evaluator worker is lazy and belongs to exactly one session. Anonymous pipes
are preferred when the parent is its only client. If a named endpoint is required,
it is placed in an owner-only directory and authenticated with an unguessable
session nonce. COM pointers never cross RPC. In-memory module metadata and PDBs are
copied to bounded owner-only temporary files and identified by content hash.

## Session ownership and state

Exactly one client owns lifecycle mutation. DAP and TUI sessions own themselves.
An MCP-launched session is MCP-owned. Other clients may observe a session, but
mutation requires explicit control transfer. An existing user-owned session is
agent-mutable only when it was created with `allowAgentControl`.

Unexpected owner disconnection has deterministic behavior:

- A launched target is terminated with its process tree.
- An attached target is detached and allowed to continue.
- A dump session is closed.
- Explicit ownership transfer supersedes these defaults.

The protocol-neutral target state machine is:

```text
Created → Starting → Running ⇄ Stopped → Terminating → Terminated
Any non-terminal state ─────────────────────────────→ Faulted
```

DAP separately tracks `Created → Initialized → Configuring → Starting` before
following the target's running and terminal states. Private RPC and MCP do not
inherit those DAP-only transitions.

Every transition occurs on the engine actor. Each stop increments a 64-bit stop
generation. Thread, frame, scope, variable, memory, and evaluation references
encode or carry that generation. A reference from an older generation produces a
typed stale-reference error; it is never resolved against new target state.

Manual pause uses `ICorDebugController.Stop`; managed stacks use the current
`ICorDebugThread3`/`ICorDebugStackWalk` contract rather than the legacy active-chain
view, which omits managed callers across native transitions. Frame COM references
are retained only for their stop generation, assigned monotonically increasing
session-local handles, and released before execution resumes. Stack paging bounds
returned work without misreporting the complete managed-frame count.

Scopes are split into receiver/arguments and lexically active locals. Parameter
names come from ECMA-335 metadata; local slot names and lifetimes come from the
current managed PDB scope at the frame's IL offset. Immediate primitive and
string formatting reads ICorDebug values without target code execution. Scope
and variable handles carry the same stop generation as their frame. Expandable
values retain canonical COM identities so repeated requests reuse stable handles;
array indexing and metadata-backed instance-field enumeration apply DAP paging
before reading child values. All retained handles are invalidated and released
before every continue.

Only pointer-like values expose DAP memory navigation; the initial managed
backend limits this to non-null arrays, matching the Microsoft adapter's rule
that scalar and ordinary object variables must not misleadingly open at their
storage address. The client receives an opaque stop-generation token. A bounded
engine operation resolves it to the current array address, applies a checked
signed offset, calls `ICorDebugProcess.ReadMemory`, and returns a contiguous
readable prefix plus the trailing unreadable count. DAP alone formats the
resolved address as hexadecimal and the bytes as base64. Writes remain
unadvertised until explicit mutation authorization and runtime safety policy
exist.

Managed frames independently expose opaque IL references. Disassembly reads the
method body from the module PE without loading target code, decodes the complete
runtime opcode catalog, formats branch and switch targets, and resolves metadata
operands only from the module's metadata reader. Validated managed-symbol sequence
points add source locations. Results always contain the DAP-requested count;
locations outside the method are explicit invalid placeholders. Session-local
virtual hexadecimal addresses distinguish methods without representing writable
process memory.

One bounded channel receives client commands and runtime callbacks. Separate
bounded channels carry target output and client events. Saturation applies
backpressure to requests and coalesces eligible progress/output notifications;
runtime stop and exit events are never dropped. Cancellation removes queued work
or invokes the operation-specific abort path. Locks are never held while waiting
for a runtime callback, process exit, compiler worker, or client write.

## CoreCLR activation and COM ownership

The distribution bundles the official dbgshim native runtime asset selected by
the package manager for its RID. The product never downloads a debugger. Dbgshim
is loaded by absolute path from the application layout, and required exports are
validated before a target starts.

Launch creates the target in the suspended state, registers for runtime startup,
resumes the process, obtains the correct debugging interface, initializes ICorDebug,
installs managed callbacks, and attaches to the created process. Unix hosts use
dbgshim's `CreateProcessForLaunch`. Windows hosts use `CreateProcessW` with a Unicode
environment and an extended-startup handle allowlist because dbgshim's convenience
wrapper neither enables Unicode environments nor exposes standard-handle inheritance. Attach
enumerates loaded CoreCLR instances, reports ambiguity explicitly, and creates the
interface for the selected runtime. Architecture and permission mismatches are
reported before partial session activation where the platform exposes enough data.

On Unix, dbgshim inherits the host's process-wide standard handles, so launch is
serialized through a short process-wide gate that redirects all three descriptors
only for the `CreateProcessForLaunch` call and restores them in nested `finally`
blocks. On Windows, `STARTUPINFOEX` assigns all three standard handles and
`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` prevents every unrelated inheritable handle
from entering the target. The parent owns dedicated anonymous-pipe endpoints and
closes every local child endpoint immediately after launch. Target output therefore
reaches bounded debugger output handling and can never share the adapter's protocol
stdout. On Unix, the launcher
starts the supervised worker with an adjacent NativeAOT `waitpid` interposer loaded
before CoreCLR initializes. A dedicated blocking waiter selects the direct child
before runtime activation and completes a nonblocking identity preflight before
the suspended target resumes. If CoreCLR's polling transport reaps that child
first, the interposer retains its exact process or signal exit code without
changing libc behavior; the blocking waiter recovers that status after `ECHILD`.
Missing or unloadable interposer assets prevent the worker from starting.

ICorDebug projections are generated from the current public IDL and checked into
the repository for offline builds. Source-generated COM and `LibraryImport` are
used where supported; ABI-sensitive interfaces use generated unmanaged vtables.
The generator records GUID, inheritance, method order, calling convention,
parameter width, and ownership annotations. CI regenerates to a temporary tree and
fails on drift.

The engine actor owns every ICorDebug reference. Acquired references are released
deterministically in reverse ownership order. A callback AddRefs only objects that
must survive callback return, enqueues the callback record, and returns promptly.
The actor drains queued callbacks and balances the runtime stop counter before
calling Continue. Shutdown disables new work, aborts eligible evaluation, detaches
or terminates, drains callbacks, releases COM, unregisters startup, closes resume
handles, and unloads no DBI component whose contract forbids unloading.

The dbgshim runtime-startup callback transfers its owned callback object into the
engine actor and returns promptly. The actor initializes ICorDebug, installs
callback interfaces 1 through 4, and attaches the selected process. Every managed
runtime callback retains only the interfaces that must outlive callback return,
queues ordered work, and returns without invoking ICorDebug directly.

## Debug Adapter Protocol

DAP messages use ASCII headers and UTF-8 JSON payloads. The transport accepts one
and only one valid `Content-Length`, bounds headers and payloads before allocation,
reads exact payload lengths, and treats truncation as a terminal protocol error.
Writes are serialized so output events cannot interleave with responses. Standard
output is protocol-only; human diagnostics use standard error.

The checked-in protocol schema is synchronized by a .NET file-based C# app. The
generator produces one documented type per file and a source-generated JSON
serializer context. Generated output is deterministic and contains the upstream
license. Schema updates are reviewed as ordinary source changes; builds do not
access the network.

Initialization follows DAP ordering exactly:

1. Validate and answer `initialize` before emitting events.
2. Accept `launch` or `attach`, begin runtime activation, and emit `initialized`
   when breakpoints and exception policy can be configured.
3. Accept configuration requests and `configurationDone`.
4. Complete the pending launch/attach response and run or stop the target according
   to configuration.

Capabilities are an allowlist computed from the active backend and session mode.
An absent, incomplete, dump-inapplicable, or platform-inapplicable feature is not
advertised. Unknown requests receive a normal unsuccessful response and do not
fault the session. Invalid sequencing receives a stable machine-readable error.

Launch configuration uses established .NET names: `program`, `cwd`, `args`,
`env`, `envFile`, `stopAtEntry`, `console`, `sourceFileMap`, `justMyCode`,
`requireExactSource`, `enableStepFiltering`, `suppressJITOptimizations`,
`symbolOptions`, `expressionEvaluationOptions`, `terminateChildProcesses`,
`processId`, and `pipeTransport`. Editors and the CLI resolve projects, launch
profiles, and tests to a concrete program before invoking DAP. The adapter does not
run builds or interpret arbitrary shell text.

DAP frame and variable IDs are compact session-local handles, not process pointers.
Paging is applied before expensive expansion. Memory references are opaque,
generation-bound tokens. `writeMemory`, `setVariable`, `setExpression`, function
evaluation, Hot Reload, and execution control are explicitly mutating operations.

## Breakpoints and execution

Source breakpoints are stored independently from bound runtime breakpoints. The
symbol index normalizes document identity without discarding original casing or
checksum information. Each matching sequence point produces a candidate method
token and IL offset. The engine chooses the closest executable location according
to DAP line/column rules, creates an ICorDebug function breakpoint, and reports the
resolved source location. Pending breakpoints rebind when modules load, update, or
reload and unbind when their module unloads.

Conditional, hit-count, and log breakpoints share the evaluator pipeline. Hit
counts are maintained per logical breakpoint and reset only when the client
replaces it. A failed condition or interpolation reports one bounded diagnostic and
follows the documented break policy; it never silently changes expression meaning.
Data breakpoints use runtime value-breakpoint support only while the underlying
storage identity is stable. Instruction breakpoints address managed IL, not machine
instructions. Their references are resolved only through a current stop generation,
must land on decoded ECMA-335 instruction boundaries, and can be expressed as an
opaque frame reference plus byte offset or a virtual address returned by disassembly.
Logical bindings retain module path, method token, and IL offset so they can rebind
after module churn. A JIT-unpatchable location remains an unverified item with the
CoreCLR diagnostic rather than failing the replacement request.

Pause maps to a balanced process stop. Continue invalidates the current stop
generation before resuming. Step operations create one active stepper for the
selected managed thread, apply sequence-point and Just My Code ranges, and disable
the stepper on completion or competing stop. Step Into targets decode calls within
the active statement and expose only same-module managed callees with managed-symbol
source. A selected occurrence combines a callee-entry breakpoint with a caller
statement guard; earlier invocations of the same callee are counted and resumed.
Target handles belong to one stop generation. Async and iterator stepping use PDB
state-machine information and runtime stepper support.

SetIP targets are visible sequence points in the active method. Discovery calls
`ICorDebugILFrame.CanSetIP` and accepts only `S_OK`, because the public CoreCLR
contract guarantees safe, correct continued execution for no other result. The
engine repeats the validation immediately before `SetIP`, then invalidates all
generation-owned handles and publishes a new stopped generation. The DAP adapter
orders the successful `goto` response before the corresponding `stopped` event.

Exception policy is compiled from DAP filters and exception options. First-chance,
user-unhandled, and unhandled events remain distinct. Exception information is
available only for the corresponding stopped generation.
Conditional stage filters accept comma-separated managed type names and match
against the exact runtime exception type and its bounded base-type hierarchy.
Plain stage filters and conditional stage filters are additive; each replacement
request atomically replaces the complete policy.

## Symbols and source

Portable and embedded PDBs are read directly with `System.Reflection.Metadata`.
Modules may supply adjacent, embedded, in-memory, or downloaded symbols. Portable
PDB symbol-store keys are derived from the module CodeView record, and every local,
cached, or downloaded candidate is independently reopened against that module
before acceptance. Configured directory and anonymous HTTP(S) searches implement
the standard `symbolOptions` module filter; Microsoft and NuGet.org servers are
opt-in. Windows PDB support uses `Microsoft.DiaSymReader` and Microsoft's packaged
native reader on Windows, including x86, x64, and ARM64. Portable identities are
validated by the metadata reader; Windows identities are validated with
`ISymUnmanagedReader5.MatchesModule`. A PDB is never accepted merely by file name.

CoreCLR symbol-update callbacks are copied synchronously into bounded immutable
Portable PDB snapshots while the target is stopped; no borrowed `IStream` or
reader-owned pointer crosses the callback boundary. Attach recovery asks
`ICorDebugModule3` for `ISymUnmanagedReader4`, copies its Portable Debug Metadata,
invokes the reader's explicit destroy boundary, and releases every COM interface.
Dynamic modules invalidate and replace that snapshot on `LoadClass`. The paired PE
image is copied through `ICorDebugProcess::ReadMemory`, validated as managed PE,
and shared by frame naming, local scopes, stepping, goto, disassembly, function
breakpoints, and managed-IL breakpoints.

Source resolution order is:

1. Checksum-valid embedded source.
2. A checksum-valid `sourceFileMap` path.
3. A checksum-valid original local path.
4. Checksum-valid Source Link content.

Raw and CRLF-normalized checksum validation is supported where compiler/source
mapping behavior requires it. `requireExactSource` rejects mismatches; otherwise a
mismatch is visibly marked and never cached as authoritative.

Symbol and source clients allow HTTPS by default, validate redirect destinations,
bound redirects, response sizes, concurrency, and total cache size, write through
atomic temporary files, and key entries by verified identity. Credentials and
query secrets are redacted. Localhost and private-address access require explicit
configuration to prevent Source Link SSRF. Paths are canonicalized beneath their
cache root, symlinks are rejected at trust boundaries, archives are bounded, and
legacy source-server command streams are never executed.

CoreCLR currently returns `E_NOTIMPL` from the public
`ICorDebugValue::CreateBreakpoint` implementation. The adapter therefore does not
advertise managed data breakpoints until the runtime supplies a portable storage
watchpoint contract; a platform-specific native watchpoint is not presented as a
language-neutral managed data breakpoint.

## Values and language services

Runtime inspection is language-neutral. Metadata provides CLR types and members;
Portable PDBs provide documents, scopes, locals, constants, imports, generated
method mappings, and language identity. The provider is selected per frame rather
than once per session, allowing mixed-language call stacks.

The integration matrix compiles and debugs checked-in C#, Visual Basic, and F#
executables in both Debug and Release configurations. Each probe binds a Portable
PDB source breakpoint, observes the runtime stop, and resolves the stopped source
frame through the same DAP and engine path used by editors.

`IDebugLanguageProvider` defines breakpoint validation, source mapping, display
formatting, expression binding, completion, and generated-code classification.
C# and Visual Basic implementations use public compiler APIs. F# uses the official
compiler service. A generic CLR provider supplies useful debugging for any other
Portable-PDB language without pretending to implement its syntax.

Language providers lower expressions to a versioned, language-neutral IR containing
literals, local/argument/`this` references, type references, field/property/member
access, indexing, conversions, unary and binary operations, calls, construction,
assignment, type tests, conditions, and interpolated log output. The engine resolves
runtime types and methods and executes the IR through ICorDebug. Compiler objects,
syntax trees, and runtime pointers never appear in the wire contract.

Pure metadata/value evaluation is preferred. Implicit property and `ToString`
evaluation use familiar .NET defaults but are disabled when the runtime cannot
safely evaluate. Only one function evaluation runs per stopped process. It has a
deadline and cancellation invokes `ICorDebugEval.Abort`; automatic `RudeAbort` is
forbidden because it can destabilize the target. An evaluation that leaves target
safety uncertain faults mutation for the session and explains why.

Expansion understands debugger display/proxy/browsable attributes, raw and results
views, root-hidden members, tuples, dynamic flags, nullable values, arrays,
collections, spans, ref structs, pointers, function pointers, closures, hoisted
locals, async state machines, object IDs, and lazy paging. Attribute evaluation is
cycle-limited and depth-limited.

C# and Visual Basic Hot Reload use compiler-produced metadata, IL, and PDB deltas.
The engine validates rude edits, applies deltas, advances module generations, and
rebinds breakpoints and active statements. F# has ordinary first-class debugging,
but Hot Reload remains unadvertised until its compiler supports the necessary EnC
contract.

## Dumps, editors, terminal, and MCP

A dump session uses the same session and value contracts but a read-only ClrMD
backend in the managed worker. `dumpPath` is mutually exclusive with live
`processId`. Threads, stacks, modules, memory, and values are available when the
dump contains them. Continue, pause, stepping, breakpoints, writes, evaluation, and
Hot Reload return typed `notSupported` errors.

VS Code registers debug type `coreclr` with label `.NET (csls)` for C#, VB, F#,
and Razor and invokes the bundled `csls debugger dap`. Zed registers the same
binary through its debug-adapter manifest, supplies a JSON configuration schema,
and translates its generic scenarios to the conventional .NET configuration.
Neither integration downloads or discovers another debugger.

The Hex1b TUI is a client of the private debugger RPC. It presents sessions,
threads, frames, source, breakpoints, variables, watches, modules, exceptions,
output, and a command palette without embedding engine logic.

MCP uses private debugger RPC rather than translating through DAP. Mutation tools
are `debug_session_start`, `debug_session_attach`, `debug_breakpoints_set`,
`debug_execution_control`, `debug_evaluate`, `debug_hot_reload`, and
`debug_session_end`. Inspection is exposed as subscribable resources beneath
`csls://debug/sessions/{sessionId}` for state, threads, stack, scopes, variables,
modules, output, and breakpoints. Results use declared structured schemas,
generation-aware pagination, progress, cancellation, and recoverable tool errors.
Tool read-only, destructive, idempotent, and open-world annotations describe real
behavior, not intent.

### MCP debugger contract

Debugger selection is independent from language-workspace selection. Every
target-dependent operation names exactly one `debug_session`; operations tied to
a stop also name the current `stopGeneration`. The MCP worker never treats the
active editor, repository, LSP session, process, or sole visible target as an
implicit debugger selection. Launch and attach create an explicit ownership
record and return its stable session identifier.

Observation and control are separate grants. Session owners may grant an MCP
connection observation, but resume, step, restart, terminate, breakpoint mutation,
assignment, function evaluation, and Hot Reload additionally require explicit
agent-control authorization for that session. Authorization is revocable, local,
time-bounded, and not inherited by another MCP connection. Tools carry truthful
MCP annotations: reads are read-only, terminate and target mutation are
destructive, and only operations with engine-enforced replay semantics are marked
idempotent.

Lifecycle tools launch, attach, open dumps, describe capabilities, and end owned
sessions. Execution tools pause, continue, step, and restart. Breakpoint tools
replace versioned breakpoint sets. Inspection tools cover threads, stacks, scopes,
variables, watches, modules, exceptions, source, memory, and disassembly. Mutation
tools evaluate with side effects, assign values, and apply Hot Reload. Each tool
has a closed input schema and a declared structured output schema; errors use
stable codes for stale generation, invalid state, unavailable symbols, unsupported
language/runtime, denied control, timeout, cancellation, and target exit.

High-cardinality state is exposed through cursor-paged resources rooted at
`csls://debug/sessions/{sessionId}`. Resource identity includes stop generation
where data expires at resume. Resource subscriptions publish bounded invalidation
notifications rather than unbounded target output. Output uses a separately paged,
bounded sequence. MCP progress represents long symbol, source, dump, and Hot Reload
operations, and cancellation reaches the engine operation rather than merely
abandoning the response.

The MCP worker supervises debugger hosts independently of language-server
workspaces. A connection has explicit caps for owned sessions, observers, retained
output, handles, subscriptions, and concurrent operations. On connection loss it
ends MCP-owned targets unless their validated launch policy permits safe detach;
it only releases observation leases for editor-owned targets. No MCP command opens
a debugger TCP listener or tunnels DAP over MCP.

### User documentation contract

The README introduces the debugger only when the managed vertical slice is the
default editor path. The documentation site then owns task-oriented pages for
setup, launch and attach, breakpoints and stepping, C#/VB/F# evaluation, symbols
and Source Link, dumps, Hot Reload, terminal workflows, MCP workflows, security,
troubleshooting, and the runtime/language/RID support matrix. Unsupported behavior
is stated directly beside each workflow.

DAP, private RPC, CLI, configuration, and MCP reference pages are generated from
shipping contracts by repository C# automation. Repository verification fails on
drift. Removing the proprietary downloader also requires migration notes for its
command, setting, cache, and editor configuration in the same pull request.

## Diagnostics, security, and privacy

User errors have stable codes, a concise message, actionable details, and optional
diagnostic data that is safe to display. Expected target and protocol failures are
not unhandled exceptions. Logs are bounded, local, redacted, and written to stderr
or an explicitly selected file. DAP and MCP stdout remain protocol-only.

Executable launch uses argument arrays and never a shell. Environment values are
not logged. Attach requires the operating system's normal permissions. Local RPC
endpoints are owner-only and authenticated. Session enumeration exposes only the
same user's sessions. Temporary metadata, sources, symbols, and dumps receive
restrictive permissions and deterministic cleanup.

All PE, PDB, dump, Source Link, DAP, and RPC inputs are untrusted. Parsers enforce
integer, recursion, allocation, count, path, and time bounds before constructing
large graphs. The debugger records no telemetry and makes no network request other
than an explicitly configured symbol or Source Link retrieval needed for the
active debugging operation.

## Packaging and compatibility

The existing tool RID matrix is authoritative. Native assets are selected by RID
at publish time and copied into a deterministic application-relative location.
Development and builds use compatible .NET 10 SDKs without an SDK-version pin.
Package versions exist only in Central Package Management where NuGet requires
them. Schema, IDL, and generated-code updates are performed by repository .NET
file-based apps and verified offline.

There is no compatibility promise for the unreleased proprietary debugger command,
setting, cache layout, or implementation. Its removal is atomic with the managed
vertical slice. DAP compatibility is capability-negotiated and additive; csls never
lies to preserve a client assumption.

## Verification model

Every capability row records three forms of evidence:

| Evidence | Requirement |
| --- | --- |
| Contract | Current public protocol, runtime, compiler, symbol, or editor contract |
| Oracle | At least one independent open implementation or consuming client |
| Probe | A real executable process, stream, socket, file, compiler, runtime, or editor test |

Contract plus observed runtime behavior wins when sources disagree. The decision
log records the discrepancy and the test that locks the behavior. Proprietary
implementation details and private issue mappings are never recorded.

The real-process matrix covers C#, VB, and F# in Debug and Release; framework-
dependent, self-contained, single-file, and ReadyToRun apps; portable, embedded,
missing, corrupt, stale, server, Source Link, in-memory, and Windows symbols;
async, iterator, closure, generic, exception, optimized, multithreaded, module
reload, Hot Reload, cancellation, and shutdown scenarios.

Protocol tests use real duplex transports and spawned adapters. Security tests pass
hostile input through the real parser or file/network boundary. Editor tests launch
the packaged adapter. Stress tests repeatedly launch and attach, force callback and
output storms, kill clients/workers/targets, and verify process, thread, handle,
temporary-file, and COM-reference cleanup.

Performance baselines measure initialization, launch-to-entry, attach-to-stop,
stop-to-stack, locals paging, cold/warm evaluation, breakpoint binding, output
throughput, resident memory, process count, and published size. Controlled runners
reject unexplained median regressions greater than ten percent. A correctness or
security improvement may override the gate only when its evidence and cost are
documented.

## Decision log

| Decision | Rationale |
| --- | --- |
| NativeAOT host plus managed evaluator worker | ICorDebug and protocol ownership benefit from a small native host; compiler services are not reliably NativeAOT-compatible |
| One engine actor | ICorDebug callbacks are serialized stops and COM ownership must remain deterministic |
| Per-frame language provider | Mixed-language stacks and future .NET languages cannot be modeled by a C# session flag |
| Direct Portable PDB reader | It is public, cross-platform, efficient, and avoids unnecessary native symbol dependencies |
| Private RPC for TUI and MCP | DAP is an editor protocol and cannot express session ownership or agent authorization precisely |
| No debugger fallback | The repository is unreleased and the product must have one testable execution path |
| No F# Hot Reload claim | The current compiler does not expose the required EnC behavior |
| IL-first instruction debugging | ICorDebug provides portable managed semantics; native/mixed-mode requires a distinct platform backend |
