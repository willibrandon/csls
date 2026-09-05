---
title: Debugger breakpoints and stepping
description: Use source, function, exception, and managed-IL breakpoints and precise stepping.
---

csls represents logical breakpoint intent separately from the CoreCLR bindings created
for each loaded module. A logical breakpoint can bind again after module unload/reload,
collectible assembly churn, or target restart without reusing stale runtime objects.

## Source breakpoints

A source breakpoint binds to executable sequence points in every loaded module whose
validated symbols identify the requested document. Breakpoint locations let editors
move a request to a valid line and column before installation. Embedded and in-memory
Portable PDBs use the same binding path as files on disk.

Each source breakpoint can carry a source-language `condition`, a `hitCondition`, or a
`logMessage`. Conditions are parsed using the language recorded in the stopped method's
PDB. An evaluation error is reported to the debugger console and exposes the stop so the
expression can be corrected.

## Conditions and hit counts

Source, function, and managed-IL instruction breakpoints support these hit-count forms:

| Form | Stops when |
| --- | --- |
| `N` | The Nth matching hit occurs. |
| `>=N` | The Nth and every later matching hit occurs. |
| `%N` | Every Nth matching hit occurs. |

`N` is a positive decimal integer. The condition is evaluated first, and only a true
result advances the logical count. Counts span all runtime bindings for that logical
breakpoint and reset when the client replaces it. One invalid request becomes an
unverified breakpoint without rejecting valid siblings.

## Logpoints

A source breakpoint with a non-empty `logMessage` logs instead of stopping. Expressions
inside `{...}` use the same side-effect-free evaluator as breakpoint conditions. Use
`{{` and `}}` for literal braces. Conditions and hit counts run before interpolation.
A successful message and an interpolation error both continue execution; errors are
written to debugger-console output.

## Function breakpoints

Function breakpoints bind managed metadata methods by their qualified name. Their
logical identity persists across module reload and restart, and they share condition and
hit-count behavior with source breakpoints. Ambiguous or unavailable methods remain
unverified rather than silently selecting an arbitrary implementation.

## Exception breakpoints

The adapter exposes distinct thrown, user-unhandled, and unhandled exception stages.
Each stage can include a comma-separated condition of managed exception type names. A
name matches that exact type and any type derived from it. Multiple plain stage filters
and conditional filter options are additive; sending a new request atomically replaces
the complete exception policy.

At an exception stop, `exceptionInfo` returns the exact managed type, message, stage,
and available stack details. Stage identity is preserved rather than reducing every
event to a generic thrown exception.

## Managed-IL instruction breakpoints

Stack frames expose opaque `instructionPointerReference` values. `disassemble` returns
ECMA-335 instructions with encoded bytes, branch targets, metadata names, and Portable
PDB source mappings. An instruction breakpoint accepts one of those references with a
signed byte offset, or a virtual address returned by disassembly.

The requested address must belong to the current stop generation and land exactly on an
instruction boundary. If CoreCLR cannot patch a valid location, csls returns that item
as unverified with the runtime diagnostic. Instruction breakpoints rebind when the
module reloads and survive tiered recompilation because their durable identity is
module, method, and IL offset—not a native code address.

## Source stepping

Step Into, Step Over, and Step Out use CoreCLR steppers and Portable PDB sequence
points. With `justMyCode` enabled, modules with matching symbols and an unoptimized JIT
policy are classified as user code. `enableStepFiltering` additionally skips property
accessors, CLR operators, and `DebuggerHidden` or `DebuggerStepThrough` members.

Async stepping follows compiler-recorded continuations across threads and competing
state-machine instances. Iterator stepping follows yield and consumer boundaries.
Presented frames use authored method identities where the PDB provides the required
state-machine mapping.

## Select a Step Into target

When one source statement contains several eligible local managed calls,
`stepInTargets` returns one generation-bound target per occurrence. Selecting a target
enters that exact call, including repeated calls to the same method on one line. Calls
without a same-module managed implementation and matching Portable PDB are omitted.

## Go to Line

`gotoTargets` offers visible sequence points in the active managed method only when
CoreCLR's `ICorDebugILFrame.CanSetIP` returns `S_OK`. The adapter repeats that safety
check immediately before `goto`. A successful response precedes the resulting stopped
event, and every offered target expires when the stop generation changes.

## Deliberately unadvertised breakpoints

Managed data breakpoints are not advertised. The public portable CoreCLR
`ICorDebugValue.CreateBreakpoint` implementation currently returns `E_NOTIMPL`; csls
does not substitute architecture-specific native watchpoints and claim equivalent
semantics. They can be added when CoreCLR exposes a portable managed storage-watchpoint
contract.

Continue with [evaluation and inspection](../debugger-evaluation/).
