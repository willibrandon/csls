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
