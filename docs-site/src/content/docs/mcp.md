---
title: MCP server
description: Connect an MCP client to a live csls workspace.
---

`csls-mcp` exposes the language intelligence and guarded edit operations from a
live `csls` session through the official Model Context Protocol C# SDK.

Start the language server in an editor, find its process identifier, then launch
the MCP server over standard input and output:

```console
csls sessions list
csls-mcp --session 12345
```

An MCP client may also pass the absolute socket path with `--socket`. Exactly one
of `--session` or `--socket` is required.

The server provides tools for diagnostics, completion, hover, navigation, symbol
search, signature help, rename, formatting, and code actions. It also provides
the current session as a resource and prompts for C# review, refactoring, symbol
explanation, diagnosis, and csls troubleshooting.

Edit tools preview changes unless the caller explicitly requests an apply step.
The session remains owned by the editor, so stopping the MCP process does not stop
the language server.
