---
title: Command line
description: Inspect and use a live csls language-server session.
---

Start `csls` in an editor, then list the sessions visible to the current user:

```console
csls sessions list
csls sessions show --session 12345
```

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
csls edit code-action Program.cs --kind quickfix --apply
```

Every machine-readable command supports `--json`. Its response uses a versioned
envelope so scripts and agents can reject shapes they do not understand.
