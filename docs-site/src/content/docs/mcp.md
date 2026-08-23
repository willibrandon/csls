---
title: MCP server
description: Connect an MCP client to a live csls workspace.
---

`csls-mcp` exposes the language intelligence and guarded edit operations from a
live `csls` session through the official Model Context Protocol C# SDK.

Start a workspace-owned session directly:

```console
csls-mcp --workspace ./MySolution.slnx
```

Direct mode starts a real language-server worker, loads the requested directory,
solution, or project, and owns that worker for the MCP process lifetime.

To use an editor-owned session, find its process identifier and attach:

```console
csls sessions list
csls-mcp --session 12345
```

An MCP client may also pass the absolute socket path with `--socket`. Exactly one
of `--workspace`, `--session`, or `--socket` is required.

The server provides tools for diagnostics, completion, hover, navigation, symbol
search, signature help, rename, formatting, code actions, workspace inspection,
restore, reload, build-host restart, and cache clearing.

Resources expose the current session and workspace plus individual projects,
documents, and document diagnostics. Prompts cover C# diagnosis, explanations,
review, refactoring, and csls troubleshooting.

Edit tools preview changes unless the caller explicitly requests an apply step.
Attached sessions remain owned by their editor. Direct sessions stop with the MCP
process.
