---
title: Debugger terminal and MCP workflows
description: Debug from the Hex1b terminal UI or through explicit, authorized MCP tools.
---

The terminal UI, DAP adapter, private debugger RPC, and MCP worker all use the same
debugger engine. They share breakpoint identity, stop generations, value formatting,
symbol policy, and target ownership behavior.

## Interactive terminal launch

Build the target, then identify an initial executable source line:

```console
csls debugger tui launch /absolute/path/to/App.dll \
  --source /absolute/path/to/Program.cs \
  --line 12 \
  --cwd /absolute/path/to/project \
  -- first argument
```

`--runtime <path>` selects a particular compatible `dotnet` host. Arguments after `--`
are passed directly to the target. The terminal opens after the initial source
breakpoint stops and shows source, managed stack, arguments, and active locals.

| Key | Operation |
| --- | --- |
| F5 | Continue |
| F6 | Pause |
| F10 | Step over |
| F11 | Step into |
| F12 | Step out |
| Ctrl+C | Close the debugger |

Closing the terminal terminates a launched target process tree.

## Interactive terminal attach

Attach to and pause one running CoreCLR process:

```console
csls debugger tui attach 12345
```

Closing the UI detaches and leaves the independently owned process running.

## MCP session selection

The `csls-mcp` package supervises one isolated debugger worker per target. Every result
contains an opaque `debugSession`; stopped-state operations also require the exact
`stopGeneration`. The server never infers a debugger target from an editor, language
workspace, or visible operating-system process.

Start a launch with `debug_session_start`, providing absolute `program` and
`workingDirectory` paths. An optional paired `initialSourcePath` and `initialLine` sets
a breakpoint before launch. Use `debug_session_attach` with one positive `processId`
for an existing target. `debug_sessions_list`, `debug_session_get`, restart, and end
operate only on sessions owned by the current MCP connection.

Disconnect deterministically terminates launched process trees, detaches attached
processes, and shuts down the isolated workers.

## Observation and control

Observation never grants target control. Read-only tools include:

- `debug_threads_get`, `debug_stack_get`, `debug_scopes_get`, and
  `debug_variables_get`;
- `debug_modules_get`, `debug_breakpoints_get`, and `debug_exception_get`;
- `debug_source_get`, `debug_memory_read`, and `debug_disassemble`;
- `debug_step_targets_get`, `debug_goto_targets_get`, and `debug_output_get`; and
- the side-effect-free `debug_evaluate` operation.

Execution, breakpoint replacement, direct assignment, function evaluation, restart,
and attached-target termination require the session's explicit `agentControl: true`
grant. Operations that act on a stop also require its exact generation. Another client
cannot reuse the grant or commandeer the session.

`debug_execute_expression` is marked destructive, non-idempotent, and open-world.
Direct assignments are destructive but do not execute target code or advance the
generation. Tool annotations describe the actual semantics instead of treating every
debugger request as read-only.

## Bounded results and stable errors

Stack, variable, and module pages accept non-negative offsets and at most 256 items.
Source, memory, disassembly, and output calls have their own explicit bounds. The newest
1,024 stdout and stderr segments are retained; cursors identify gaps if older output
was evicted.

Expected failures carry stable `_meta.errorCode` values such as
`debugger_control_denied`, `debugger_invalid_state`, `debugger_request_invalid`, and
`debugger_stale_generation`. Successful results provide structured content and a
matching JSON text representation.

## Resources and subscriptions

`csls://debug/` resource templates expose the same connection-owned session, output,
breakpoint, thread, stack, scope, variable, module, exception, source, memory, and
disassembly projections. Stopped-state URIs include the exact generation so a saved URI
cannot silently inspect a later stop.

Clients using the current MCP protocol can listen to exact owned URIs through
`subscriptions/listen`. Resource update notifications come from engine state, output,
and breakpoint-binding events; csls does not poll the target. Legacy subscription RPCs
are not exposed.

The prompts `diagnose_dotnet_debugger_failure`, `plan_dotnet_breakpoints`, and
`explain_dotnet_debugger_state` gather bounded read-first evidence for one explicit
session. They contain no hidden execution or breakpoint changes.

For every tool schema, resource URI, prompt, annotation, and shared transport convention,
see the generated [MCP reference](../mcp-reference/).
