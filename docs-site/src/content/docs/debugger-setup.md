---
title: Debugger setup and lifecycle
description: Configure launch, attach, restart, and remote .NET debugging with csls.
---

The csls debugger ships with the `csls` tool. It uses the public CoreCLR debugging
contracts bundled for the installed platform and does not download a debugger when a
session starts.

## Check the installation

Run the packaged component probe after installation or an update:

```console
csls debugger doctor
```

The command verifies the platform runtime shim and every native entry point required
for launch and attach. A failure normally means that the tool package does not match
the current operating system or architecture, or that the installation is incomplete.

## Build before launch

csls launches one concrete managed executable or assembly. It deliberately does not
run `dotnet build`, interpret a launch profile, select a test, or pass arguments through
a command shell. Build the target first and use an absolute `program` path.

When `program` is a managed `.dll`, csls uses the configured `runtimeHost` or the
compatible `dotnet` host resolved from the environment. A platform executable is
started directly. Arguments are passed as an array without shell interpretation.

## VS Code launch

The csls extension registers the `coreclr` debug type. A minimal `.vscode/launch.json`
entry is:

```json
{
  "version": "0.2.0",
  "configurations": [
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
  ]
}
```

`cwd` defaults to the directory containing `program`. An environment property with a
string value adds or replaces that variable; a `null` value removes it from the target
environment. `runtimeHost` may identify an existing absolute host executable when the
normal `dotnet` resolution is unsuitable.

Set `noDebug` to `true` to use the same direct process launcher without attaching
CoreCLR.

## VS Code attach

Attach selects one already-running process by operating-system identifier:

```json
{
  "name": ".NET Attach",
  "type": "coreclr",
  "request": "attach",
  "processId": 12345
}
```

The process must run CoreCLR, be accessible to the current user, and match the
debugger host architecture. Attach never takes ownership of the target. Disconnecting
detaches and lets it continue unless a client explicitly requests termination through
an operation that the adapter advertises and authorizes.

## Zed launch and attach

The Zed extension registers the `csls` adapter. Put a launch entry in `debug.json`:

```json
[
  {
    "label": ".NET Launch",
    "adapter": "csls",
    "request": "launch",
    "program": "/absolute/path/to/bin/Debug/net10.0/App.dll",
    "cwd": "/absolute/path/to/project",
    "args": ["first argument"]
  }
]
```

For attach, use `"request": "attach"` and a positive `"processId"`. Zed starts the
configured csls binary with `debugger dap`; the extension does not discover or install
a second debugger.

## Runtime behavior options

The launch and attach requests accept these source-stepping policies:

| Property | Default | Behavior |
| --- | --- | --- |
| `justMyCode` | `true` | Treat symbol-bearing, unoptimized modules as user code and skip other modules during source stepping. |
| `enableStepFiltering` | `true` | Skip property accessors, CLR operators, and members marked with debugger step-filter attributes. |
| `suppressJITOptimizations` | `false` | For launch only, request unoptimized JIT code for modules with validated symbols. |

CoreCLR accepts JIT optimization suppression only during module load, so it is not an
attach option. The `modules` response reports the effective policy instead of assuming
the request succeeded.

## Restart and ownership

The standard DAP `restart` request accepts the latest nested launch or attach
configuration. Logical breakpoints survive restart, while every frame, variable,
memory, instruction, and execution-target handle is invalidated. Stop generations
remain monotonic so an old identifier can never resolve against the replacement target.

A restarted launch terminates the debugger-owned process tree and creates a new one. A
restarted attach detaches and reattaches without terminating the independent process.
If the editor or adapter exits unexpectedly, csls terminates a launched process tree
but only detaches from an attached process.

## Remote and container targets

Run `csls debugger dap` in the environment where the target runs and transport its
standard streams through the editor's existing SSH, container, or remote channel. The
debugger does not expose a TCP listener. Paths in the DAP configuration are paths in the
target environment; use `sourceFileMap` when build-time source paths differ from editor
paths.

Continue with [breakpoints and stepping](../debugger-breakpoints/) or configure
[symbols and source retrieval](../debugger-symbols/).
The generated [DAP reference](../debugger-dap-reference/) lists every implemented
request, advertised capability, and editor configuration property.
