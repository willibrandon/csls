---
title: Debugger evaluation and inspection
description: Inspect managed values, evaluate expressions, call target code, and assign values safely.
---

Every stopped-state result belongs to a monotonically increasing stop generation.
Frames, scopes, variables, memory references, and execution targets from an earlier
generation are rejected instead of being resolved against new runtime state.

## Threads, stacks, scopes, and variables

The adapter enumerates real CoreCLR threads and managed frames. Portable PDB scopes
supply argument and lexically active local names. Object expansion reads exact runtime
types and walks the complete derived-to-base hierarchy, including base classes declared
in another loaded assembly. Arrays are paged and retain their live rank and dimensions.

Values expose `evaluateName` only when csls can construct a valid source expression for
that value. Expansion and evaluation therefore share the same field and array identity
instead of manufacturing expressions that the evaluator cannot resolve.

## Side-effect-free expressions

DAP `evaluate`, breakpoint conditions, logpoint interpolation, and the MCP
`debug_evaluate` tool share one language-neutral evaluator. C#, Visual Basic, and F#
syntax is parsed in a supervised compiler worker selected from the frame's PDB language
identity, then lowered to a bounded versioned plan. Compiler objects and syntax trees
never cross into the runtime engine.

The side-effect-free subset includes:

- literals, locals, arguments, and `this` or `Me`;
- instance fields and managed array indexes;
- built-in unary, binary, comparison, and short-circuit Boolean operators;
- conditional selection;
- explicit built-in primitive conversions; and
- language-valid checked integral constants and numeric widening used by assignment.

C# casts, Visual Basic `CType` and predefined conversions, and F# numeric conversion
functions lower to the same runtime operation. An unknown Portable PDB language receives
the portable CLR subset for locals, arguments, fields, and indexes.

This path never calls a property, operator overload, conversion method, `ToString`, or
other target code. The private `debugger/evaluate` RPC and MCP `debug_evaluate` reject a
plan that could execute target code.

## Explicit target-code evaluation

DAP evaluation can explicitly call a qualified instance method or loaded-type static
method when CoreCLR permits function evaluation at the selected frame. Explicit C#
`new T(...)`, Visual Basic `New T(...)`, and F# `new T(...)` expressions can construct a
loaded non-generic runtime type.

The binder searches exact metadata signatures, including inherited instance methods in
another module. Arguments can be CLR primitives, `null`, current-generation object and
array references, or literal and side-effect-free computed strings. Strings are
allocated in the target with their exact UTF-16 length, including embedded NUL values.
An ambiguous overload or type name fails before execution.

Only the selected managed thread runs during a call. One function evaluation can be
active at a time and has a five-second deadline. Cancellation and timeout call
`ICorDebugEval.Abort`, await the completion callback, and never escalate to
`RudeAbort`. A returned value, target exception, or cooperative abort advances the stop
generation because target code may allocate, collect, or mutate state. If cooperative
abort cannot restore a trustworthy stop, the session faults.

MCP separates these semantics into `debug_execute_expression`, which requires
an active time-bounded grant from `debug_agent_control_set` and the exact current
generation. The tool is correctly marked
destructive, non-idempotent, and open-world because invoked target code may mutate
state outside the debuggee.

## Assignment

DAP `setVariable` and `setExpression`, plus MCP `debug_variable_set` and
`debug_expression_set`, write directly to a stopped local, argument, instance field, or
managed array element. The right-hand side uses the side-effect-free evaluator.

Supported values are exact primitives, checked contextual integral literals,
language-valid built-in numeric widening, explicit primitive conversions, `null`, and
an existing runtime reference with the same displayed type. Direct writes do not run
target code, so they preserve the generation and publish variable invalidation for
aliased editor views.

Constructing a replacement object or string, boxing, reference conversions, and
user-defined conversions are not accepted by direct assignment. Use an explicit,
authorized function evaluation only where its mutation and generation-change semantics
are appropriate.

## Completion

DAP completion comes from the exact stopped frame, not the editor's language workspace.
Root completion includes locals, arguments, and language-appropriate literals. Member
completion first resolves a side-effect-free receiver, then walks its exact CoreCLR type
hierarchy and loaded metadata. An explicitly qualified loaded type supplies supported
static members.

Results are prefix-filtered, capped at 2,048 candidates, carry UTF-16 replacement
spans, and use case-insensitive matching for Visual Basic. If `frameId` is omitted, the
selected stopped thread's top managed frame is used.

## Value presentation

All surfaces use one exact CoreCLR type formatter. It preserves generic arguments,
array dimensions, tuple shape, nullable underlying types and values, enum storage and
flags, and `decimal` scale. `DateTime`, `DateTimeOffset`, `TimeSpan`, and `Guid` are
reconstructed from validated runtime fields and formatted invariantly in the debugger
host. A local `DateTime` is labeled without borrowing the host machine's time-zone
offset.

Strings and characters deterministically escape quotes, backslashes, NUL, standard
controls, remaining control code points, and unpaired UTF-16 surrogates while preserving
valid Unicode scalar pairs. Presentation does not execute implicit target formatting.

Object expansion honors `DebuggerBrowsableAttribute` on runtime fields. `Never` hides
a field from the default view, `Collapsed` preserves ordinary expansion, and
`RootHidden` flattens expandable children into the containing page. Null, scalar,
cyclic, and depth-limited root-hidden values remain directly inspectable. A virtual
Raw View exposes every physical field without presentation transforms.

## Memory and disassembly

Managed arrays expose an opaque memory reference for their owning generation.
`readMemory` accepts signed offsets and returns at most 1 MiB per DAP request. Primitive
and ordinary object values do not advertise arbitrary memory navigation, and
`writeMemory` is not supported.

Managed frames expose an instruction reference for exact-count ECMA-335 disassembly.
The result includes encoded bytes, branch labels, metadata symbols, and Portable PDB
source mappings. This is architecture-independent managed IL, not native machine code.

## Current evaluator boundaries

Generic object construction, object and collection initializers, properties, user-defined
operators and conversions, `DebuggerDisplay` and `DebuggerTypeProxy`, LINQ results
views, object IDs, and automatic `ToString` calls are not advertised. These features
require complete binding, lifetime, recursion, and side-effect policies; csls does not
execute them implicitly.
