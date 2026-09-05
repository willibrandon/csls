---
title: Debugger setup and lifecycle
description: Configure launch, attach, restart, and remote .NET debugging with csls.
---

The csls debugger ships with the `csls` tool. It uses the public CoreCLR debugging
contracts bundled for the installed platform.

## Check the installation

Run the packaged component probe after installation or an update:

```console
csls debugger doctor
```

The command verifies the platform runtime shim and every native entry point required
for launch and attach. Use the tool package that matches the current operating system
and architecture.

## Build before launch

csls launches one concrete managed executable or assembly. Build the target first
and use an absolute `program` path.

When `program` is a managed `.dll`, csls uses the configured `runtimeHost` or the
compatible `dotnet` host resolved from the environment. A platform executable is
started directly. Arguments are passed directly as an array.

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
environment. Set `runtimeHost` to an absolute host executable path to choose the
`dotnet` host that runs the application.

Set `noDebug` to `true` to run the target as an ordinary process.

Set `stopAtEntry` to `true` to stop at the first executable entry-point statement.
The default is `false`. Continue from that stop to run the application with its
configured breakpoints. Restart applies the launch configuration's entry-stop setting.

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
debugger host architecture. Disconnecting detaches and leaves the process running.
Clients can explicitly request termination through an advertised, authorized operation.

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
configured csls binary with `debugger dap`.

## Runtime behavior options

Configure source stepping and module policy with these options:

| Property | Default | Behavior |
| --- | --- | --- |
| `justMyCode` | `true` | Treat symbol-bearing, unoptimized modules as user code and skip other modules during source stepping. |
| `enableStepFiltering` | `true` | Skip property accessors, CLR operators, and members marked with debugger step-filter attributes. |
| `suppressJITOptimizations` | `false` | During launch, request unoptimized JIT code for modules with validated symbols. |
| `enableHotReload` | `false` | During launch, prepare symbol-bearing modules for compiler-driven Hot Reload. |

With `suppressJITOptimizations: true`, csls requests unoptimized JIT code during module load.
The `modules` response reports the effective policy through `isOptimized` and
`symbolStatus`.

CoreCLR applies the Edit and Continue policy during module load.
Set `enableHotReload` on a launch to receive compiler-produced Hot Reload
updates. Module inspection reports `isHotReloadEnabled`, `hotReloadGeneration`, and a
bounded `symbolStatus` diagnostic.

## Restart and ownership

The standard DAP `restart` request accepts the latest nested launch or attach
configuration. Logical breakpoints survive restart. Refresh frames, variables,
memory, instructions, and execution targets after restarting. Each replacement
target uses a newer stop generation.

A restarted launch terminates the debugger-owned process tree and creates a new one. A
restarted attach detaches and reattaches to the independently running process.
If the editor or adapter exits unexpectedly, csls terminates launched process trees
and detaches from attached processes.

## Remote and container targets

Run `csls debugger dap` in the environment where the target runs and transport its
standard streams through the editor's existing SSH, container, or remote channel.
Paths in the DAP configuration are paths in the
target environment; use `sourceFileMap` when build-time source paths differ from editor
paths.

Continue with [breakpoints and stepping](../debugger-breakpoints/) or configure
[symbols and source retrieval](../debugger-symbols/).
The generated [DAP reference](../debugger-dap-reference/) lists supported
requests, advertised capabilities, and editor configuration properties.
