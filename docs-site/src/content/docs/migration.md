---
title: Migration
description: Move an editor and workspace to csls.
---

csls connects to editors through standard LSP and loads workspaces through Roslyn
and MSBuild. To switch an editor, install the tool and update its server command
and configuration section.

## Editor change

Install `csls`, then replace the C# language-server command with:

```console
csls lsp
```

Use `csls` as the configuration section. The `csharp` section is also read when an
editor already emits it. Workspace roots, positions, and document versions remain
standard LSP values.

Run `csls doctor` from the workspace before changing an editor. It verifies SDK
selection and loads the same Roslyn workspace used by the server. Then use
`csls sessions list` after the editor starts to confirm that the live session is
discoverable.

## Workspace selection

The workspace is selected from the path sent by the client. SDK projects honor
`global.json`. File-based apps honor their directives. Legacy project evaluation uses an installed compatible
Visual Studio, Build Tools, or Mono host when Roslyn requires one.

csls builds Roslyn state from solutions, projects,
source files, editor overlays, and current configuration each time a session starts.
