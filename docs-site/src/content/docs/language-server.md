---
title: Language server
description: Language Server Protocol features implemented by csls.
---

`csls` advertises only capabilities backed by an active implementation. Current
language features include:

- C# compiler and analyzer diagnostics, plus Razor syntax diagnostics
- completion with import edits, negotiated snippets, lazy documentation, hover, and signature help
- definitions, declarations, implementations, references, highlights, links, monikers, and linked editing
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

`textDocument/moniker` returns `dotnet` identifiers built from canonical assembly
identities and Roslyn documentation IDs. Strong-named assembly APIs are unique
within the scheme. Unsigned project APIs are unique within their project group,
while non-public symbols use project or document scope.

`textDocument/linkedEditingRange` links matching start and end names in XML
documentation, including nested and custom elements. Self-closing, mismatched,
and unrelated text do not produce linked ranges.

Razor views and components are parsed by the compiler from the pinned .NET SDK.
Pull diagnostics follow the current unsaved `.cshtml` or `.razor` snapshot and
return to the persisted file after the editor closes it.

[Configuration](../configuration/) is pulled through the standard workspace request
when the client supports it. Push-only clients use the same settings and precedence.

## Session control

Each language-server process creates a private Unix domain socket and a session
manifest in the user cache directory. The socket is supported by .NET on Windows,
Linux, and macOS. It is not exposed over the network.

The CLI and MCP server authenticate through operating-system file permissions and
connect to this socket. This keeps editor requests, terminal commands, and agent
requests on one Roslyn workspace.
