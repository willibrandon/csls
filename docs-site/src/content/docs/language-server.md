---
title: Language server
description: Language Server Protocol features implemented by csls.
---

`csls` advertises only capabilities backed by an active implementation. Current
language features include:

- compiler and analyzer diagnostics
- completion, hover, and signature help
- definitions, declarations, implementations, references, and highlights
- document and workspace symbols
- semantic tokens with full and delta responses
- call hierarchy, type hierarchy, selection ranges, and inlay hints
- rename, code actions, and document formatting

The server tracks open-document versions and applies incremental text changes.
Workspace loading supports solutions, projects, and loose C# files.

## Session control

Each language-server process creates a private Unix domain socket and a session
manifest in the user cache directory. The socket is supported by .NET on Windows,
Linux, and macOS. It is not exposed over the network.

The CLI and MCP server authenticate through operating-system file permissions and
connect to this socket. This keeps editor requests, terminal commands, and agent
requests on one Roslyn workspace.
