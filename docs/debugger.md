# Debugging .NET applications

csls includes a CoreCLR debugger for VS Code, Zed, terminals, and MCP clients.
These clients share the same engine, breakpoints, stepping, source mapping, and
variable inspection. C#, Visual Basic, and F# receive language-aware expression
evaluation. Other managed languages use CLR metadata and Portable PDBs for source
debugging and runtime inspection.

Use a debugger package that matches the target's operating system and architecture.
Check the installation with:

```console
csls debugger doctor
```

## Terminal launch

Build the application, then provide its executable or assembly, an initial source
file, and a one-based executable line:

```console
csls debugger tui launch ./bin/Debug/net10.0/App.dll \
  --source ./Program.cs \
  --line 12 \
  -- argument-one argument-two
```

Use `--cwd <directory>` to choose the target working directory and
`--runtime <path>` to select a compatible `dotnet` host. Arguments after `--`
are passed directly to the target.

Use `--env-file <path>` to load UTF-8 environment assignments before launch.
Relative file paths resolve from `--cwd`.

Use `--show-raw-values` with terminal launch or attach to inspect physical runtime
fields in objects, tuples, and collections. Editor configurations and MCP session
start or attach requests select this view with
`"expressionEvaluationOptions": { "showRawValues": true }`.

Use `--allow-implicit-func-eval false` to keep automatic value inspection in the
debugger host. Editor configurations and MCP session requests select this policy
with `"expressionEvaluationOptions": { "allowImplicitFuncEval": false }`.
The default, `true`, permits authorized debugger proxy construction and property
evaluation. Explicit expression calls and Results View expansion retain their
target-execution authorization. Both presentation options persist across restart.

The terminal opens when the initial breakpoint stops the process. Select a thread
and stack frame to inspect its source, arguments, and locals. The auxiliary view
shows target output, modules, breakpoints, watches, or the current exception.
F1 opens a searchable command palette.

| Key | Operation |
| --- | --- |
| F1 | Open the command palette |
| F2 | Cycle output, modules, breakpoints, watches, and exception views |
| F5 | Continue |
| Shift+F5 | Terminate the target |
| F6 | Pause |
| F9 | Toggle a breakpoint at the source cursor |
| F10 | Step over |
| F11 | Step into |
| F12 | Step out |
| Ctrl+C | Close the debugger |

Each terminal instance works with one explicitly selected target. Closing a launch
session terminates its process tree.

Use `--stop-at-entry` to begin at the application's first executable entry statement:

```console
csls debugger tui launch ./bin/Debug/net10.0/App.dll --stop-at-entry
```

## Terminal attach

Attach to a running CoreCLR process by its operating-system identifier:

```console
csls debugger tui attach 12345
```

The debugger pauses the process and opens the terminal. Closing an attached session
detaches and leaves the process running.

On Linux ARM64, native frame inspection also requires `ptrace` authorization.
With Yama's restricted attach policy, the target can use `PR_SET_PTRACER` to
authorize the debugger process and its descendants. The kernel also checks the
target's credentials and dumpability.

## Editor integration

VS Code and Zed start the Debug Adapter Protocol host with:

```console
csls debugger dap
```

The adapter reads DAP frames from standard input, writes DAP frames to standard
output, and writes diagnostics to standard error.

In VS Code, use a `coreclr` launch configuration:

```json
{
  "name": ".NET Launch",
  "type": "coreclr",
  "request": "launch",
  "program": "/absolute/path/to/bin/Debug/net10.0/App.dll",
  "cwd": "/absolute/path/to/project",
  "args": ["first argument"],
  "env": {
    "APP_ENVIRONMENT": "Development"
  }
}
```

Set `envFile` to load assignments from a UTF-8 file of at most 1 MiB. Relative
paths resolve from `cwd`. File assignments override inherited variables, and
explicit `env` entries take precedence over file assignments. Restart rereads
the file. MCP `debug_session_start` accepts the same file through
`environmentFilePath`.

In Zed, use `"adapter": "csls"` in `debug.json`. For attach, set
`"request": "attach"` and provide a positive `processId`.

Set `stopAtEntry` to `true` in a launch configuration to inspect the entry frame
before continuing. The same option is available on MCP `debug_session_start`.

Build projects and resolve launch profiles before starting the adapter. A managed
DLL runs through the selected `dotnet` host; a platform executable starts directly.
Restart retains logical breakpoints and starts a new stop generation. Refresh
frames and variables after restarting.

For remote or container debugging, run the adapter in the target environment and
carry its standard streams through the editor's remote connection. Use
`sourceFileMap` to map build-time source paths to local source paths.

See [setup and lifecycle](../docs-site/src/content/docs/debugger-setup.md) and the
[generated DAP reference](../docs-site/src/content/docs/debugger-dap-reference.md)
for configuration details.

## Breakpoints, inspection, and source

Set source, function, managed-IL instruction, and exception breakpoints. Conditions
use the selected frame's source language. Hit conditions accept `N`, `>=N`,
and `%N` for the Nth matching hit, the Nth and later matching hits, and every
Nth matching hit. Logpoints write interpolated values to debugger output.

Inspect threads, stacks, modules, arguments, locals, fields, arrays, and managed IL.
Expressions and completions use the selected stopped frame. Explicit method calls
and object construction use guarded target execution. Direct assignments update
stopped target storage.

Use an explicit cast to inspect an exactly matching boxed struct. Assign the
result to a local, field, or array element of the same runtime type to copy its
value. Embedded reference fields keep the identity of their referenced objects.

Use type-qualified expressions such as `System.Int32.MaxValue` and
`System.String.Empty` to inspect constants and static fields. Static-field
completions use the selected frame's language, and assignments to mutable static
fields update the target's storage.

Matching Portable PDBs provide source locations on Windows, Linux, and macOS.
Matching Windows PDBs provide source locations on Windows. Symbol stores, embedded
source, and Source Link supply validated symbols and source text. Configure trusted
symbol locations and authorize private-network source endpoints explicitly.

`requireExactSource` defaults to `true` for launch and attach. Source breakpoints
use local files matching their PDB checksums. Set it to `false` to use edited
local source; source views label that content as unverified. The same option is
available on MCP session start and attach, and through the terminal's
`--require-exact-source false` option.

The detailed guides cover
[breakpoints and stepping](../docs-site/src/content/docs/debugger-breakpoints.md),
[evaluation and inspection](../docs-site/src/content/docs/debugger-evaluation.md),
and [symbols and source](../docs-site/src/content/docs/debugger-symbols.md).

## MCP debugging

Configure the MCP client to run `csls-mcp`. Each debugger session runs in an
isolated worker.

1. Call `debug_session_start` with absolute `program` and `workingDirectory`
   paths, or call `debug_session_attach` with a `processId`.
2. Use the returned `debugSession` to select that target on subsequent calls.
3. Inspect threads, stacks, scopes, and variables with the session's current
   `stopGeneration`.
4. Before changing the target, explicitly grant control with
   `debug_agent_control_set`, `enabled: true`, and a `durationSeconds` value
   from 1 through 3,600.
5. Use execution and breakpoint tools with that grant and the required stop
   generation. Refresh stopped-state handles after execution.
6. End the session with `debug_session_end`.

Control grants belong to one session and MCP connection. Grants expire after their
specified duration and can be revoked with `enabled: false`. Closing the MCP
connection terminates its launched process trees, detaches its attached targets,
and shuts down its workers.

`debug_dump_open` opens an existing dump for managed thread, stack, module, and
captured-value inspection. Expand objects and structs through their instance fields
and arrays through their indexed elements. Resource subscriptions deliver updates
from debugger events.
Authorized `debug_hot_reload` calls apply compiler-produced C# and Visual Basic
updates to launch modules prepared with `enableHotReload: true`.

See [terminal and MCP workflows](../docs-site/src/content/docs/debugger-terminal-mcp.md)
for usage and [the MCP reference](../docs-site/src/content/docs/mcp-reference.md)
for tool schemas and resource URIs. The
[compatibility and security guide](../docs-site/src/content/docs/debugger-compatibility.md)
covers platforms, process ownership, and source validation.
