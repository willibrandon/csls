---
title: Command line
description: Inspect and use a live csls language-server session.
---

## Workspace doctor

Inspect the SDK and Roslyn workspace that csls will use without starting an editor:

```console
csls doctor
csls doctor src/MyApp --json
csls doctor src/MyApp --binlog artifacts/doctor.binlog
```

The command selects the SDK from the target directory, starts a transient csls
server, loads the real MSBuild workspace, and reports projects, documents,
diagnostics, build hosts, and startup logs. Source errors are reported as warnings
because the language server can still serve a project that does not compile.
Startup, SDK, project-load, and requested-build failures return a nonzero exit code.
`--binlog` also runs `dotnet build` and writes an MSBuild binary log for detailed
evaluation and build analysis.

## Live sessions

Start `csls` in an editor, then list the sessions visible to the current user:

```console
csls sessions list
csls sessions show --session 12345
csls sessions watch
csls sessions watch --json
```

`sessions watch` writes an initial snapshot and then reports each added, updated,
or removed session until canceled. JSON mode writes one complete response envelope
per line, so agents can process the stream without waiting for it to close. Each
event includes its sequence, the changed session, and the current bounded session
snapshot.

When exactly one session is live, query commands infer it. Otherwise, pass its
process identifier with `--session`.

```console
csls query hover Program.cs --line 8 --character 20
csls query diagnostics Program.cs --json
csls query references Program.cs --line 8 --character 20
csls query symbols CustomerService
```

Other query commands cover completion, definition, declaration, type definition,
implementation, selection range, highlights, document symbols, and signature
help. Position arguments use zero-based UTF-16 line and character offsets, which
match LSP positions.

## Workspace maintenance

Workspace commands use the same live control service as the editor and dashboard:

```console
csls workspace restore --workspace src/MyApp
csls workspace reload --session 12345
csls workspace restart-build-host --workspace src/MyApp/MyApp.csproj
csls workspace clear-cache --session 12345 --json
```

`restore` runs the real .NET CLI before reloading Roslyn. Reload and build-host
restart preserve unsaved document text. `--workspace` may name a loaded root or
a path inside it.

## Requests and traces

List live requests before canceling one by its stable correlation identifier:

```console
csls requests list --session 12345
csls requests cancel 4ef66b8d-6e71-48df-96df-7b0e53d3801b --session 12345
```

Request cancellation reaches the token owned by the active Roslyn operation.
The result reports whether the request was still live when cancellation was
delivered.

Tracing records bounded request lifecycle data in memory:

```console
csls trace start --session 12345
csls trace stop --session 12345 --json
```

Each retained entry includes its correlation identifier, protocol operation,
workspace generation, duration, completion state, and cancellation state. A new
trace replaces the previous trace. Trace data is not written to disk.

## Safe edits

Rename, formatting, and code actions return a preview by default. Add `--apply`
only after reviewing that result:

```console
csls edit rename Program.cs Customer --line 8 --character 20
csls edit rename Program.cs Customer --line 8 --character 20 --apply
csls edit format Program.cs --apply
csls edit code-action Program.cs --kind quickfix --line 8 --character 20
csls edit code-action Program.cs --kind quickfix --line 8 --character 20 --apply
```

Missing-using quick fixes search the loaded project and its references, then keep
only imports that make the unresolved type bind to the intended Roslyn symbol.
The same command implements the interface selected by the supplied position.
Source actions such as `source.organizeImports` do not require a target position.

Every machine-readable command supports `--json`. Its response uses a versioned
envelope so scripts and agents can reject shapes they do not understand.

## Agent commands

Create a reusable `SKILL.md` with the supported CLI and MCP workflows:

```console
csls agent init
csls agent init --path .agents/skills/csls/SKILL.md
csls agent init --stdout
```

Existing files are left unchanged unless `--force` is passed. `--json` reports
the absolute output path in the standard versioned response envelope.

After installing the separate `csls-mcp` tool, start it through the main command:

```console
csls agent mcp --workspace ./MySolution.slnx
csls agent mcp --session 12345
```

The command also accepts an absolute control socket with `--socket`. Exactly one
connection option is required.
