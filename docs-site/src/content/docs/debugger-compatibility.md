---
title: Debugger compatibility and security
description: Understand supported .NET targets, platforms, language behavior, security boundaries, and known limits.
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
| Dumps | Read-only MCP sessions for bounded managed thread, stack, and module inspection. |
| Hot Reload | Compiler-produced C# and Visual Basic updates on explicitly enabled launch modules. F# debugging is supported, but F# compiler delta emission is not yet claimed. |

Source-language behavior depends on the PDB language identity. C#, Visual Basic, and F#
receive compiler-parsed expressions and language-appropriate completion matching. Other
languages retain runtime stacks, metadata names, Portable PDB source mapping, locals,
fields, arrays, stepping, and the portable side-effect-free expression subset.

The target and debugger must have the same architecture. A container or remote target
runs the debugger in that target environment; csls does not provide a cross-architecture
proxy or network listener.

## Process and protocol security

- Programs are executed directly without a command shell.
- DAP and MCP standard output contains protocol frames only; diagnostics use standard error.
- Runtime native libraries are resolved from the installed package layout rather than an arbitrary target directory.
- The product records no telemetry.
- A launch is owned and its process tree is terminated on disconnect; attach is non-owning and detaches by default.
- Frames, values, memory, and execution destinations are opaque and stop-generation-bound.
- Function evaluation is serialized, deadline-limited, cooperatively aborted, and never escalated with `RudeAbort`.
- MCP observation is separate from explicit, per-session mutation and execution authorization.

## Untrusted symbols and source

PE files, PDBs, source documents, Source Link maps, DAP messages, and MCP inputs are
untrusted. Readers enforce input, recursion, allocation, page, redirect, and response
limits. Symbol and source identities are verified before content is cached or shown.
Temporary files use owner-scoped locations and atomic publication.

Symbol-server URLs are anonymous bases. Source Link sends no debugger-managed cookies
or credentials. HTTPS cannot downgrade, redirects cannot escape the configured
authority, and private-network access requires a specific opt-in rule.

## Capabilities not advertised

Clients must use the capabilities returned by DAP `initialize`. The debugger currently
does not advertise:

- managed data breakpoints, because public portable CoreCLR storage breakpoints return
  `E_NOTIMPL`;
- native machine-code disassembly or mixed native debugging;
- reverse execution, step back, or arbitrary instruction-pointer changes;
- arbitrary memory writes;
- generic object construction, initializers, or implicit property and `ToString` calls;
- debugger display/proxy attributes, results views, or object IDs.

Unsupported requests return a protocol error. csls does not download or fall back to a
proprietary debugger for them.

Hot Reload is launch-only and requires `enableHotReload: true`, a runtime module that
accepts Edit and Continue policy, and one compiler-produced metadata, IL, and minimal
Portable PDB generation. The compiler must reject rude edits and provide exact active
statement mappings. DAP has no standard apply-update request; the authorized MCP
workflow applies C# and Visual Basic updates. Ordinary F# debugging remains supported,
but F# Hot Reload is not advertised until its compiler service exposes complete delta
emission and active-statement mapping.

## Migration from another `coreclr` adapter

The VS Code extension registers the standard `coreclr` type, so basic launch and attach
configurations use familiar `program`, `cwd`, `args`, `env`, and `processId` properties.
Keep paths absolute. Move project building, launch-profile expansion, and test selection
to the editor task that starts debugging.

Replace adapter-specific remote transports with an invocation of `csls debugger dap` in
the target environment. Replace proprietary debugger path/download settings with the
installed csls tool and validate it using `csls debugger doctor`. Keep only options
documented in [setup](../debugger-setup/) and [symbols](../debugger-symbols/); csls rejects
unknown behavior rather than approximating another adapter's private extension.

## Startup troubleshooting

Run:

```console
csls debugger doctor
csls --version
dotnet --info
```

Then verify that the `csls` executable selected by the editor is the intended install,
the target path is absolute and exists in the adapter's environment, and the target
architecture matches the tool package. Editor output should show diagnostics from
standard error without any non-protocol standard output.

For attach failures, confirm that the process runs CoreCLR and that operating-system
permissions allow the current user to debug it. For source failures, inspect module
`symbolStatus` and follow the [symbols and source checklist](../debugger-symbols/). For
stale-frame or stale-variable errors, refresh threads, stack, scopes, and variables from
the newest stop instead of reusing handles from before continue, step, restart, or
function evaluation.
