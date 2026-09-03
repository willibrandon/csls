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
