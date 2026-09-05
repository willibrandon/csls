---
title: Debugger evaluation and inspection
description: Inspect managed values, evaluate expressions, call target code, and assign values safely.
---

Every stopped-state result belongs to a monotonically increasing stop generation.
Native frame bindings, scopes, variables, memory references, and execution targets
from an earlier generation are rejected. Logical frame identifiers can survive
debugger-owned evaluation when the same physical frame remains stopped; csls reacquires
their native bindings. Application execution retires those identifiers, and MCP
inspection always requires the exact current generation.

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
loaded non-generic or closed generic runtime type, including nested generic and
array type arguments.

The binder searches exact metadata signatures, including inherited instance methods in
another module. Arguments can be CLR primitives, `null`, current-generation object and
array references, or literal and side-effect-free computed strings. Strings are
allocated in the target with their exact UTF-16 length, including embedded NUL values.
An ambiguous overload or type name fails before execution.

Only the selected managed thread runs during a call. One function evaluation can be
active at a time and has a five-second deadline. Cancellation and timeout call
`ICorDebugEval.Abort`, await the completion callback, and never escalate to
`RudeAbort`. A returned value, target exception, or cooperative abort advances the stop
generation because target code may allocate, collect, or mutate state. The same stopped
physical frame keeps its logical identifier while csls reacquires native bindings and
refreshes variable views. Resuming or stepping the application retires frame identifiers.
If cooperative abort cannot restore a trustworthy stop, the session faults.

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
an existing runtime reference with the same runtime type. Direct writes do not run
target code, so they preserve the generation and publish variable invalidation for
aliased editor views.

Reference compatibility uses loaded runtime type identity, including generic arguments
and assembly load context. Matching type names alone do not permit a write. Direct
writes to managed by-reference and native pointer locations are rejected.

An existing unboxed struct can be copied into a destination of the same loaded runtime
type, including tuples and `Nullable<T>`. Copies preserve the entire value, including
nullable presence and managed reference fields, and are limited to 1 MiB. Tuple names
come from the destination declaration. Returned expandable values retain the original
destination storage, with array indices evaluated before the write.

A C# `default` literal or Visual Basic `Nothing` can be assigned directly to reset
the destination to its default value. Primitive and struct storage is cleared through
CoreCLR's original value home, including nullable presence and managed reference
fields. Object references become null. Ref-like values such as spans can be cleared
without copying references from another scope. These writes preserve the stop
generation and destination tuple names. C# `null` remains distinct from Visual
Basic's `Nothing` conversion to non-nullable value types.

Ref-like copies and member writes into register-backed structs are rejected. Whole
register-backed values use the runtime's write operation and report its restrictions.
Validation failures preserve existing Results View snapshots; an attempted runtime
write retires them, including when the runtime reports a write failure. DAP clients
that advertise `supportsInvalidatedEvent` receive variable refresh events after a
write attempt. MCP resource subscriptions receive change notifications.

String, call, and construction expressions can also supply an assignment value through
explicitly authorized target execution. The debugger then reacquires the destination
frame, applies the assignment, and returns the new generation. Boxing, complete
reference conversions, and user-defined conversions remain unsupported.

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

`DebuggerDisplayAttribute` formats value, name, and type columns from bounded
instance-field paths without target execution. Type, member, and assembly declarations
are supported. Display labels and type overrides affect presentation; expressions and
assignments bind actual source names and runtime types. Tuples retain authored element
names where symbol metadata supplies them, with logical paging and a physical Raw View.

`DebuggerTypeProxyAttribute` constructs the selected proxy through guarded function
evaluation. Its visible fields and non-indexed properties share ordinal ordering,
root-hidden members flatten into the view, and Static members groups accessible
static values. Raw View preserves the original object. Proxy constructors and property
getters can run arbitrary target code and advance the stop generation.

Enumerable objects expose a lazy Results View when the target has loaded the runtime's
enumeration debug view. Listing the row does not run enumeration. Expanding it runs the
selected `IEnumerable<T>` or `IEnumerable` implementation through guarded evaluation,
materializes the enumeration into a non-lazy snapshot, and invalidates generation-owned
scope, variable, and memory handles. Unchanged physical frames retain their logical identifiers.
Pages, refreshed scopes, and expression inspection of the same receiver reuse that
snapshot. Target execution or a direct debugger assignment retires the snapshot
and its child and memory handles.
Page size does not limit target enumeration. The row displays an
execution warning and carries lazy and side-effect presentation hints. Empty results
display an Empty message; target exceptions retain a usable stopped session. Arrays,
strings, and successful debugger proxies use their existing presentations.

MCP `debug_variables_get` remains read-only, including after a control grant. Use
`debug_variables_get_presented` with an active `debug_agent_control_set` grant and the
exact generation to construct proxies or expand Results View. This tool returns the
replacement generation after execution. Resolving Results View returns one replacement
variable with the snapshot reference and child counts. Use that reference and generation
with `debug_variables_get` or the variables resource to read snapshot pages without
another control grant or target execution.

## Memory and disassembly

Managed arrays expose an opaque memory reference for their owning generation.
`readMemory` accepts signed offsets and returns at most 1 MiB per DAP request. Primitive
and ordinary object values do not advertise arbitrary memory navigation, and
`writeMemory` is not supported.

Managed frames expose an instruction reference for exact-count ECMA-335 disassembly.
The result includes encoded bytes, branch labels, metadata symbols, and Portable PDB
source mappings. This is architecture-independent managed IL, not native machine code.

## Current evaluator boundaries

Object and collection initializers, general property expressions, user-defined operators
and conversions, object IDs, and automatic `ToString` calls are not advertised.
Debugger proxy properties and Results View use the explicit presentation policy
described above; ordinary expression inspection remains side-effect-free.

The untyped C# `default` literal requires an assignment destination. Evaluating it
alone reports a missing type context. Typed `default(T)` expressions and full
target-type inference remain outside the supported subset.
