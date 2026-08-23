---
title: Language server
description: Language Server Protocol features implemented by csls.
---

`csls` advertises only capabilities backed by an active implementation. Current
language features include:

- compiler and analyzer diagnostics
- completion with import edits, negotiated snippets, lazy documentation, hover, and signature help
- definitions, declarations, implementations, references, and highlights
- document and workspace symbols
- semantic tokens with full and delta responses
- call hierarchy, type hierarchy, selection ranges, and inlay hints
- rename, code actions, and document formatting

The server tracks open-document versions and applies incremental text changes.
Workspace loading supports solutions, projects, loose C# files, multiple roots,
and folder changes during a live session. Unsaved documents survive reloads when
their workspace folder remains active.

Completion edits are computed by Roslyn. Clients that advertise snippet support
receive snippet insertion text with Roslyn's final caret position. Other clients
receive plain text. `completionItem/resolve` adds Roslyn documentation without
changing the edit, sort text, or filter text returned by the original request.

[Configuration](../configuration/) is pulled through the standard workspace request
when the client supports it. Push-only clients use the same settings and precedence.

## Session control

Each language-server process creates a private Unix domain socket and a session
manifest in the user cache directory. The socket is supported by .NET on Windows,
Linux, and macOS. It is not exposed over the network.

The CLI and MCP server authenticate through operating-system file permissions and
connect to this socket. This keeps editor requests, terminal commands, and agent
requests on one Roslyn workspace.
