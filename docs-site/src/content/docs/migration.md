---
title: Migration
description: Understand the clean C# implementation and move an editor to csls.
---

csls is a clean C# implementation. Protocol behavior is carried over as executable
tests, then implemented with current Roslyn, StreamJsonRpc, System.CommandLine, and
.NET hosting APIs. Old source layout, build machinery, and runtime assumptions are
not copied into this repository.

The migration is capability based. A feature is complete when the server advertises
it, its normal and failure behavior are covered over the real transport, and editor,
CLI, or MCP integration uses that implementation. The server never advertises a
placeholder capability.

## Editor change

Install `csls`, then replace the C# language-server command with:

```console
csls lsp
```

Use `csls` as the configuration section. The `csharp` section is also read when an
editor already emits it. Workspace roots, positions, and document versions remain
standard LSP values, so editor-specific adapters are not required.

Run `csls doctor` from the workspace before changing an editor. It verifies SDK
selection and loads the same Roslyn workspace used by the server. Then use
`csls sessions list` after the editor starts to confirm that the live session is
discoverable.

## Behavior ledger

Ported behavior is tracked privately and expressed publicly through the feature
tests and [language-server reference](../language-server/). Protocol parity tests
cover the expected result shapes. Additional tests cover current .NET features,
Razor, file-based apps, multi-root workspaces, project file operations, Native AOT
packaging, CLI control, MCP, and terminal editors.

`$/csharp/debugInfo` remains available for test synchronization and diagnosis. It
reports current state rather than exposing implementation-specific objects.

## Workspace differences

The workspace is selected from the path sent by the client, not from the directory
that happened to launch the editor. SDK projects honor `global.json`. File-based
apps honor their directives. Legacy project evaluation uses an installed compatible
Visual Studio, Build Tools, or Mono host when Roslyn requires one.

No state database is migrated. csls rebuilds Roslyn state from solutions, projects,
source files, editor overlays, and current configuration each time a session starts.
