# Interactive .NET debugger REPL design

## Purpose and quality objective

The debugger includes a transcript-first read-evaluate-print loop for investigating
live managed processes and captured dumps. Developers can inspect a frame, refine
an expression, set a breakpoint, step, and compare observations in one continuous
conversation with the debugger. The REPL complements the spatial source/stack/value
terminal UI and the editor Debug Console.

The quality requirement is a world's-best-in-class open-source .NET debugger REPL.
That requirement means demonstrable language correctness, responsive editing,
predictable execution, useful diagnostics, accessibility, and recovery under real
debuggee failures. The acceptance criteria below define the engineering bar;
comparative claims require recorded measurements and independent workflow evidence.

This document extends the [debugger architecture](debugger-design.md). Its engine,
language, ownership, security, NativeAOT, and packaging contracts remain authoritative.
REPL operations use the same runtime backend as DAP, terminal inspection, and MCP.
Compiler services belong to the supervised evaluator worker. Target expressions
bind to the actual selected frame, loaded modules, and runtime types.

## Entry points and selection

`csls debugger repl` opens an unselected prompt with contextual help and session
discovery. Explicit `--session`, `--process-id`, `--dump`, or `--program` selectors
choose an existing session, attach a process, inspect a dump, or launch a program.
These selectors are mutually exclusive. Launch options share their definitions
and validation with the debugger CLI; `--` separates program arguments.

A new launch or attach creates a REPL-owned session. Connecting to an existing
editor or MCP session begins as an observer. Control requires the authoritative
owner's explicit grant or ownership transfer. The prompt displays session identity,
process identity, live/dump mode, state, language, and observation/control role.
The selected thread, frame, and stop generation remain visible in the status line.
Narrow layouts use short local labels with a command to display full identities.

An unselected prompt can list sessions and manage local help/history preferences.
Selecting a session is always an explicit action. Selection is local to each
client: changing the REPL frame preserves another client's frame selection.
Execution and mutation notifications update every observing client.

`:ui` changes between transcript and pane layouts while preserving the same client
owner, session, drafts, watches, and connection. Detaching, ending a session, and
leaving the REPL are separate commands with clear target-lifetime effects. EOF and
connection failure use the shared owner cleanup rules for every REPL-owned session:
launched targets terminate, attached targets detach, and dumps close. Observation
leases end independently of the observed owner's session.

## Commands and expression cells

Debugger commands start with `:`. Other input is a source-language expression in
the selected frame. This separation keeps application identifiers such as `next`,
`help`, and `threads` available as expressions. `:help` explains syntax, current
state requirements, control requirements, and available language/backend operations.
Documented aliases expand to one canonical command before validation.

| Workflow | Canonical prompt surface | Shared operation |
| --- | --- | --- |
| Session and context | `:sessions`, `:session`, `:threads`, `:thread`, `:stack`, `:frame` | Discovery, session snapshots, generation-owned inspection |
| Source and values | `:source`, `:locals`, `:inspect`, `:more`, expression input | Source resolution, scopes, paged variables, pure evaluation |
| Repeated investigation | `:watch`, `:capture`, `:compare` | Pure watches and bounded historical snapshots |
| Execution | `:pause`, `:continue`, `:step`, `:next`, `:out`, `:restart`, `:wait` | Authorized execution and stop notifications |
| Breakpoints | `:break`, `:breakpoints`, `:exceptions` | Versioned source/function/IL breakpoint and exception policies |
| Explicit mutation | `:execute`, `:set`, `:present`, `:hotreload` | Guarded target evaluation, assignment, presentation, compiler deltas |
| Runtime inspection | `:modules`, `:exception`, `:memory`, `:disassemble` | Bounded runtime/module/memory/managed-IL inspection |
| Session lifetime | `:launch`, `:attach`, `:dump`, `:detach`, `:end`, `:quit` | Explicit host ownership and cleanup |
| Local interaction | `:help`, `:history`, `:clear`, `:export`, `:source-commands`, `:ui` | Prompt, transcript, reviewed command files, and layout |

Commands that change a breakpoint preserve independently owned breakpoint sets
through the shared version/ownership contract. A stale replacement receives a
conflict result with the current version before any change is applied.

The command parser uses System.CommandLine. It preserves the original expression
text and source spans following the command's expression delimiter. Quoting and
escaping are consistent across interactive input, startup arguments, and command
files. Process launches use explicit argument arrays. Shell operators remain input
to the declared command or language grammar.

Each submission is a numbered cell. Its record includes the original input,
language, session identity, frame identity, starting generation, ending generation,
mutation revision, operation identifier, outcome, and bounded typed results.
Parse/bind diagnostics identify exact source spans and return the complete draft
for correction. A submission receives one terminal outcome, including when a
target, worker, transport, or output consumer exits during evaluation.

## Language-aware editing

C#, Visual Basic, and F# have first-class frame-aware expressions, completion,
signature information, and diagnostics. The active PDB language chooses the
provider; an explicit language override is visible and validated. Mixed-language
frame changes select the matching provider. Other managed languages use their
declared provider capabilities and CLR metadata inspection.

Submission completeness belongs to the compiler provider. It distinguishes a
complete expression, incomplete input, and invalid syntax, including raw and
interpolated strings, comments, nested constructs, and F# indentation. Multiline
cells retain line breaks and indentation. Source-language evaluation semantics
remain compiler-owned rather than reimplemented in the terminal editor.

Enter submits a complete cell and continues incomplete input. A documented newline
binding inserts a line break without submission. Blank input performs no execution.
Bracketed paste inserts one editable payload and waits for deliberate submission,
including when the payload contains trailing newlines or debugger commands.
Submitting several commands requires an explicit command-file or batch action.

The editor supports selection, clipboard operations, word movement, indentation,
undo/redo, search, matching delimiters, and discoverable configurable key bindings.
History navigation preserves both the current draft and edited working copies of
older cells. Multiline input stays one history entry. Completion acceptance and
cell submission are separate actions when the completion menu is open.

Completion reads frame names and metadata through the shared side-effect-free
pipeline. It shows useful type/member information and exact replacement spans.
It never executes getters, enumeration, proxy constructors, or target formatting.
Each request carries an input revision and context generation; newer edits or
context changes cancel outstanding work and retire stale suggestions. Rendering
and input processing proceed independently of compiler or symbol work.

Accepting completion applies the compiler's replacement text, source spans, and
caret position as one undoable edit. Display labels serve presentation only.
Conversions and other expression-wide rewrites preserve the surrounding cell.
Typing filter characters while a menu is open requires resolving the selected
item against the current draft before committing it. Completion descriptions and
caches carry the same language, session, frame, generation, mutation revision,
and input identity as the corresponding suggestion.

## Evaluation, results, and safety

Bare expressions use pure inspection. A cell that requires target execution gets
a source-local explanation and an explicit execution action. `:execute` authorizes
the selected cell through the existing guarded evaluation path. `:set` uses the
shared typed assignment binder; `:present` authorizes getter/proxy/enumeration work.
The prompt clearly distinguishes pure inspection, direct mutation, and target-code
execution. Authorization is rechecked inside the session-operation queue.

Automatic completion, history search, result rendering, comparison, and stop-time
watch refresh use pure operations. Target-code execution can perform externally
visible work and cannot be rolled back by undoing a cell. The transcript retains
the original execution outcome, changed generation, and mutation revision after
an exception or cancellation. Undo edits prompt text only.

Every submitted inspection captures its exact session, stop, and frame identity.
Execution or Hot Reload retires live result handles according to the engine's
generation rules. Historical result text remains readable with its original
context. Expanding an expired result offers an explicit fresh evaluation rather
than interpreting an old handle against the new stop. Direct assignments also
invalidate cached values using the mutation revision.

`:capture baseline -- expression` stores a bounded immutable observation with type,
source context, capture policy, and completeness metadata. `:compare baseline -- expression`
compares a new observation and distinguishes changed, unavailable, and uncaptured
values. Stored observations contain copied data rather than retained target roots.
Reference values are represented by captured fields and context; cross-stop object
identity requires an explicit engine-supported object identity, not a raw address.
`:watch` retains expression definitions and reports each new stop independently.

Results provide language-aware scalar text, exact runtime types, expandable pages,
raw/presented labels, and source navigation. Large strings, arrays, recursive
graphs, and deep stacks use the existing bounded readers. Paging reports the next
cursor and whether a total is known. Dumps use the same pure inspection and snapshot
workflow against immutable captured state.

Ctrl+C has a state-specific, visible action:

| Prompt state | Action |
| --- | --- |
| A request is active | Cancel that request and keep the prompt responsive while cancellation settles |
| The selected controlled target is running | Request a normal debugger pause |
| The prompt is idle or observation-only | Cancel the local draft or selection |

The engine decides whether cancellation can safely resume inspection. Cooperative
evaluation abort, target exit, and uncertain runtime state retain their original
outcomes. A lost response after execution begins is reported as indeterminate;
reconnection never automatically replays the submitted mutation. Queued commands
have individual cancellation and bounded storage.

## Shared architecture

`Csls.App` owns REPL startup and common CLI option definitions.
`Csls.Debugger.Repl` owns the documented command model, submission state,
transcript records, history policy, and mapping to typed debugger operations.
`Csls.Debugger.Terminal` owns Hex1b editing, completion menus, and rendering.
`Csls.Debugger.Contracts` and `Csls.Debugger.Control` own transport-safe operations,
notifications, capability information, and ownership validation. Compiler-backed
completion and binding stay in the existing managed evaluator worker.

Components have explicit ownership: an input editor owns the draft; a submission
coordinator owns the active operation; a transcript owns bounded immutable records;
and a session selection owns its observation lease. They exchange values and events.
Production assemblies communicate through documented contracts. Internal visibility
is reserved for tests. A broad partial class sharing these components' mutable
fields is not the ownership model.

The Hex1b event loop is the single writer of rendered state. Background compiler,
RPC, history, and output tasks post immutable updates to that loop. Session
notifications drive refresh; a running target requires no session polling loop.
All subscriptions and pending work have explicit asynchronous disposal. A late
completion cannot overwrite the selected session, input revision, or frame.

The REPL front end follows the existing NativeAOT/RID policy. It neither loads
target assemblies into the front end nor hosts a second compiler/runtime engine.
New backends participate through the same capabilities and inspection contracts.

### Editor Debug Consoles and MCP

DAP `evaluate` with `context: "repl"` binds expression cells to the editor's selected
frame and its existing DAP session. Explicit colon commands are interpreted only
in that context and are constrained to that owning session. Its command allowlist
covers inspection, watches, breakpoints, execution, evaluation, and assignment;
editor launch/attach/disconnect retain their DAP lifecycle operations. Layout,
session switching, and local file/history management belong to the terminal client.
Shared semantic dispatch returns DAP responses and ordered events through the
adapter's writer. Other evaluate contexts keep their expression meaning. The
editor's input widget remains owned by the editor; terminal editing widgets belong
to the terminal REPL.

MCP keeps its typed debugger tools, structured schemas, generation checks, control
grants, progress, and cancellation. REPL commands resolve to those same operations;
there is no generic execute-arbitrary-command tool that bypasses their annotations.
Any MCP transcript access is explicit, owner-scoped, paged, and read-only. Transcript
content, expressions, and target output are untrusted data, never authorization
or instructions to execute. Interactive execution grants do not confer agent control.

## Output, automation, and privacy

The transcript has tagged command, result, diagnostic, target-stdout, and
target-stderr records. One renderer preserves prompt text, caret, selection,
scroll position, and completion state while target output arrives. Bounded output
eviction emits a visible sequence-gap marker; command outcomes and stop/exit state
retain their independently bounded records. Target stdin has explicit focus and
routing separate from debugger command input. An interactive target console uses
an explicitly focused, isolated terminal surface; its terminal control sequences
are confined to that surface and its clipboard access requires user authorization.

Target-controlled text is escaped before entering the transcript. ANSI/OSC control
sequences cannot alter the prompt, clipboard, hyperlinks, or title. File/source
navigation requires an explicit user action and validated identity. Color is
optional; textual state labels, an accessible linear presentation, keyboard-only
operation, Unicode-aware cursor movement, and predictable narrow-window behavior
are required. Console presentation uses Hex1b's detected reflow policy; emulator
tests exercise the corresponding explicit reflow strategies.

Redirected input selects deterministic batch presentation using the same parser
and dispatcher. `--commands <file>` explicitly selects a UTF-8 command file.
`--format jsonl` writes versioned structured records without terminal escapes;
human diagnostics go to stderr, and target output remains tagged records.
Execution commands require explicit batch control authorization. Command files
have their own source locations, stop on the first failure, and await each
operation's defined completion before issuing the next one. `:wait stopped` and
`:wait exited` await authoritative state with cancellation and a bounded deadline.
State inspection and notification subscription are coordinated so a fast stop or
exit cannot be missed. A terminal state that prevents the requested stop settles
the wait with an explicit outcome. Execution sequences wait before frame-dependent reads.

Command files are inert until explicitly selected. Paths are bounded, validated
regular files and never implicitly discovered or loaded from a workspace. A full
syntax/permission preflight precedes the first executable command; runtime binding
and generation validation happen again for each operation. A later failure reports
the last completed command and its effects. Exports create new files atomically
and require explicit overwrite selection for an existing destination.

In-memory history is available immediately. Persistent input history and transcript
export are opt-in, use owner-only storage, and have separate controls. Target values,
launch environments, and captured output are not automatically persisted with input
history. Sensitive-input controls exclude individual cells; redaction is best-effort
and never presented as proof that arbitrary expressions contain no secrets. History
merges concurrent writers atomically, preserves recoverable valid records after
corruption, and reports storage errors without losing the editable draft.

Each input cell has an explicit history disposition: excluded, memory-only, or
persistent when persistence is enabled. That disposition also governs transcript
export and optional local history suggestions. Export previews identify omitted
cells; sensitive input requires separate deliberate inclusion. History search
supports incremental and prefix matching with visible language/session context.
Recalling an entry restores editable text and requires a fresh submission and
current authorization. Concurrent appends, truncation, and file replacement are
reconciled under the history store's bounded cross-process write coordination.

The prompt and command-file submission limit is 1 MiB of UTF-8. Transcript retention is
bounded by both 10,000 records and 16 MiB; in-memory input history by 1,000 cells
and 1 MiB. Captured observations share a 16 MiB per-client budget with explicit
eviction and release. Engine/RPC limits remain authoritative when lower. The
product sends no telemetry and performs no prompt, history, or result upload.

## Acceptance and performance

These are delivery requirements, verified with MSTest 4 and `dotnet test` through
Microsoft.Testing.Platform. Tests use real debugger hosts, compiler workers,
programs, dumps, sockets, files, and terminal clients. Backend substitutions and
mock terminal services do not establish acceptance.

Hex1b headless tests run the real application with `WithHeadless()` and declared
dimensions. `Hex1bTerminalAutomator` drives keyboard, paste, mouse, and resize
actions. Condition-based waits observe the intended rendered state with bounded
cancellation. Snapshot assertions check text, cursor, selection, clipping, colors
where meaningful, and the live generation, not merely the presence of a prompt.
Actual PTY/ConPTY process tests additionally prove packaging, stdin routing,
terminal restoration, and signal/EOF behavior. Failures retain the automator's
step diagnostics, terminal snapshot, and bounded debugger transcript. Cleanup
awaits the app, subscriptions, workers, and exact owned target lifetimes.

| Acceptance area | Required observable evidence |
| --- | --- |
| Languages | C#/VB/F# Debug/Release and mixed-language stacks; exact expression values, contextual completion spans, overload diagnostics, and language changes |
| Editing | Multiline strings/comments/indentation, hostile paste, history draft recovery, undo/redo, Unicode graphemes, wide characters, resize/reflow, and accessible linear output |
| Completion commits | Compiler-provided casts and expression rewrites, filter typing before acceptance, exact caret placement, single-step undo, and stale descriptions after frame or language changes |
| History privacy | Concurrent processes, file replacement/truncation, corrupt tails, excluded and memory-only cells, export previews, and recalled execution commands preserve the selected storage and execution policy |
| Execution safety | Getters, proxies, enumeration, and calls have observable target-side counters; pure editing/reads leave them unchanged; explicit actions execute exactly once |
| State and ownership | Competing stops, assignment, Hot Reload, session/frame switching, expired handles, revoked grants, and independent editor/MCP selections preserve exact identities |
| Failure recovery | Compiler/worker/target death, canceled reads/evaluation, broken pipes, output floods, full history storage, malformed files, and EOF settle every operation and release ownership |
| Captured investigation | Live and dump observations retain exact values/types, paging, unavailable-value distinctions, and bounded cross-stop comparisons |
| Client agreement | Equivalent REPL, DAP, and MCP operations yield matching semantic results, errors, revisions, and target effects on the same fixtures |
| Automation | Interactive and batch inputs share parsing and errors; preflight and partial execution are explicit; tagged JSONL preserves correlation and safe output |

Controlled performance runs establish cold and warm baselines for first usable
prompt, input-to-paint, local command dispatch, completion, evaluation, paging,
cancellation feedback, output storms, resize, history search, and session teardown.
Initial targets are p95 input-to-paint within 16 ms, warm completion within 200 ms,
and visible cancellation acknowledgement within 100 ms. Native abort completion is
measured separately. First usable prompt targets are 500 ms warm and 1 second cold;
launch/attach and symbol loading proceed as separately reported operations.
Runner hardware, terminal dimensions, corpus sizes, and cold/warm definitions
are recorded with each baseline. The parent ten-percent regression gate applies.

Stress tests fill the actual history/transcript/capture budgets, interrupt writes,
page large values, sustain target output while editing, and repeatedly switch and
close sessions. Assertions cover bounded retained memory, native handles, threads,
processes, subscriptions, and temporary files after teardown. Correct cancellation
and rendering must survive saturation. Every supported shipping RID participates
in the applicable real-process and packaging checks under the existing CI budgets.

Independent prompt and debugger workflow comparisons cover editing, completion,
error correction, command discoverability, evaluation, interruption, and cleanup.
Compiler/runtime contracts and actual target effects decide semantic disagreements.
Workflow comparisons and measured usability improvements accompany performance
evidence before making a best-in-class product claim.

## Delivery and documentation

Implementation first establishes the shared command/context contracts and pure
live/dump inspection. Subsequent increments add guarded mutation and execution,
editor-console integration, batch operation, persistence, comparison, and measured
accessibility/performance acceptance. Each increment retains the existing debugger
and CI requirements. The design adds a client experience to the existing engine.

Shipping documentation provides launch/attach/dump examples, a prompt walkthrough,
keyboard help, expression and execution examples, output/input routing, automation,
history/privacy controls, and recovery instructions. Command help and reference
pages are generated from the implemented command/schema definitions. The README
and docs site describe available workflows with verified executable examples.
