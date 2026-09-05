# csls

<!-- mcp-name: io.github.willibrandon/csls-mcp -->

`csls` is a Native AOT C# language server, command-line interface, and agent
platform for .NET developers. It targets .NET 10 and is designed for terminal
editors, IDEs, automation, and AI agents.

File-based apps are evaluated by the selected .NET SDK, including package,
project, include, property, and SDK directives.

Windows, Linux, and macOS on x64 and arm64 are first-class build and test
targets. Real Fresh, GNU Emacs/Eglot, Helix, and Neovim sessions exercise the
server through Hex1b terminal automation. Real VS Code and Zed processes cover
graphical editor clients.

The complete 1.0 feature set is implemented, and no unimplemented LSP capability
is advertised by the server.

The bundled editor-independent .NET debugger launches or attaches to managed
processes without downloading a proprietary debugger. VS Code and Zed use the
same `csls debugger dap` engine for source breakpoints, pause and continue,
source stepping, stacks, modules, arguments, locals, fields, arrays, function
breakpoints, hit-count breakpoints, bounded array-memory inspection, symbolic
managed-IL disassembly, and instruction breakpoints across C#, Visual Basic, F#,
and other
Portable-PDB-producing .NET languages. Managed exception filters accept exact
and base type names. Matching Portable PDBs on every platform and Windows PDBs on
Windows can be loaded from trusted local or HTTP symbol stores with identity
validation and bounded caching. Source-aware
Step Into can select a particular local managed
call on a statement, and Go to Line exposes only destinations that CoreCLR certifies
for safe continued execution. Launches can suppress JIT optimizations for loaded
managed modules, and module inspection reports the effective runtime policy.
The installed MCP server exposes explicit debugger lifecycle, execution, and
bounded inspection tools through isolated supervised workers. Agents can inspect
threads, stacks, scopes, variables, and modules; execution changes require an
explicit per-session control grant and the current stop generation. The same
contract supports deterministic source, function, instruction, and exception
breakpoint replacement, generation-safe target restart, authoritative breakpoint
inspection, plus bounded source, memory, managed-IL, exception, and execution-target
inspection. Cursor-addressable stdout and stderr remain available through both a
tool and debugger resource after bursts or target termination.
Current MCP subscriptions deliver debugger resource invalidations from engine
events without polling.
Read-only MCP dump sessions inspect bounded managed thread, stack, and module evidence
after the target has terminated. Authorized MCP clients can apply compiler-produced
C# and Visual Basic Hot Reload generations to explicitly enabled launch modules,
including exact on-stack remapping and breakpoint rebinding. Managed data breakpoints
and debugger visualizer attributes remain unadvertised
until their complete runtime semantics are available. Run
`csls debugger tui launch` or `csls debugger tui attach` for the interactive
Hex1b source, thread, stack, arguments, locals, watch, module, breakpoint, exception,
output, and command-palette workflow.

Read the [csls documentation](https://willibrandon.github.io/csls/) for editor,
CLI, MCP, and development guidance.

Install the language server and MCP server as .NET tools:

```console
dotnet tool install --global csls
dotnet tool install --global csls-mcp
```

Native AOT packages are selected automatically for supported Windows, Linux,
and macOS hosts. Standalone archives and container images are published with
each release.

Run `csls dashboard` while a language-server session is active to inspect its
workspaces, projects, documents, diagnostics, live requests, bounded traces,
caches, and logs. The CLI and dashboard can cancel requests, control tracing,
restore, reload, restart build hosts, and clear caches through the same live
control service.

Run `csls doctor [path]` to verify SDK selection and load the target through a
real transient language-server session. Add `--binlog <path>` when an MSBuild
binary log is needed.

Configure an MCP client to run the separate `csls-mcp` tool without arguments.
Each target-dependent tool call selects exactly one `workspace`, `session`, or
`socket`, so one MCP connection can work across several repositories and live
editor sessions. Run `csls agent init` to create reusable instructions for a
coding agent.

## Build

```console
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
```
