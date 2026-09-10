---
title: Debugger compatibility and security
description: Choose .NET targets, platforms, and clients, and understand debugger security.
---

## Support matrix

| Area | Support |
| --- | --- |
| Runtime | Live CoreCLR processes whose architecture matches the debugger host. |
| Languages | Language-aware C#, Visual Basic, and F#; portable CLR inspection for other managed languages with CLR metadata and Portable PDBs. |
| Windows | x64 and ARM64 NativeAOT packages; x86 ReadyToRun package. Portable and identity-matched Windows PDBs. |
| Linux | glibc x64/ARM64 and musl x64/ARM64 NativeAOT packages. Portable PDBs. |
| macOS | x64 and ARM64 NativeAOT packages. Portable PDBs. |
| Editors | VS Code and Zed through the same standard-input/output DAP host. |
| Other clients | Any DAP client that negotiates and uses the capabilities returned by `initialize`. |
| Terminal | Source, threads, stack, variables, and auxiliary views for the selected debug session. |
| Dumps | Read-only MCP sessions for bounded managed thread, stack, and module inspection. |
| Hot Reload | Compiler-produced C# and Visual Basic updates on explicitly enabled launch modules. |
| Evaluation | Side-effect-free inspection, plus explicit method calls and construction of loaded non-generic or closed generic types through guarded target execution. |
| Presentation | Bounded `DebuggerDisplay` and `DebuggerBrowsable` support, guarded `DebuggerTypeProxy` expansion, Raw View, and lazy enumerable Results View snapshots. |

Source-language behavior depends on the PDB language identity. C#, Visual Basic, and F#
receive compiler-parsed expressions and language-appropriate completion matching. Other
languages retain runtime stacks, metadata names, Portable PDB source mapping, locals,
fields, arrays, stepping, and the portable side-effect-free expression subset.

The target and debugger must have the same architecture. A container or remote target
runs the debugger in that target environment and carries DAP over the editor's connection.

See [evaluation and inspection](../debugger-evaluation/) for supported expressions,
presentation behavior, and the separate MCP authorization required to execute target code.

## Process and protocol security

- Programs are executed directly with an argument array.
- DAP and MCP messages use standard output. Diagnostics use standard error.
- Runtime native libraries are resolved from the installed package layout.
- Disconnecting terminates a launched process tree. Disconnecting an attached session detaches and leaves its target running.
- Scope, value, memory, instruction, and execution-target handles are opaque and stop-generation-bound.
- Unchanged physical frames retain their logical identifiers across debugger-owned evaluation.
  Refresh frame identifiers after application execution. MCP inspection uses the current `stopGeneration`.
- Function evaluation is serialized, deadline-limited, and cooperatively aborted.
- MCP observation is separate from explicit, per-session mutation and execution authorization.

## Untrusted symbols and source

PE files, PDBs, source documents, Source Link maps, DAP messages, and MCP inputs are
untrusted. Readers enforce input, recursion, allocation, page, redirect, and response
limits. Symbol and source identities are verified before content is cached or shown.
Temporary files use owner-scoped locations and atomic publication.

Symbol-server URLs are anonymous bases. Source Link uses anonymous requests with
bounded redirects within the configured authority and preserves HTTPS transport.
Private-network access requires a specific opt-in rule.

## Hot Reload configuration

For Hot Reload, launch with `enableHotReload: true` and a runtime module that accepts
Edit and Continue policy. Supply one compiler-validated metadata, IL, and minimal
Portable PDB generation with exact active statement mappings. The authorized MCP
`debug_hot_reload` workflow applies C# and Visual Basic updates.

## Migration from another `coreclr` adapter

The VS Code extension registers the standard `coreclr` type, so basic launch and attach
configurations use familiar `program`, `cwd`, `args`, `env`, and `processId` properties.
Keep paths absolute. Move project building, launch-profile expansion, and test selection
to the editor task that starts debugging.

Replace adapter-specific remote transports with an invocation of `csls debugger dap` in
the target environment. Replace proprietary debugger path/download settings with the
installed csls tool and validate it using `csls debugger doctor`. Configure the options
documented in the [DAP reference](../debugger-dap-reference/) and
[symbols](../debugger-symbols/).

## Startup troubleshooting

Run:

```console
csls debugger doctor
csls --version
dotnet --info
```

Then verify that the `csls` executable selected by the editor is the intended install,
the target path is absolute and exists in the adapter's environment, and the target
architecture matches the tool package. The adapter writes diagnostics to standard
error and DAP frames to standard output.

For attach failures, confirm that the process runs CoreCLR and that operating-system
permissions allow the current user to debug it. For source failures, inspect module
`symbolStatus` and follow the [symbols and source checklist](../debugger-symbols/). For
stale-frame or stale-variable errors, refresh threads, stack, scopes, and variables from
the newest stop.
Debugger-owned function evaluation also invalidates scope, variable, memory, and
execution-target handles. Unchanged physical frames keep their logical identifiers
for refreshing scopes. MCP requests use the replacement stop generation with those
frame identifiers.
