---
title: csls
description: C# language intelligence for editors, terminals, and agents.
---

`csls` is a C# language server with a command-line interface and an MCP server.
It uses Roslyn for language intelligence and ships as Native AOT .NET tools for
Windows, Linux, and macOS.

The language server works over standard input and output. A private Unix domain
socket lets the CLI and MCP server share the editor's live workspace and compiler
process.

Use csls with Fresh, GNU Emacs with Eglot, Helix, Neovim, VS Code, Zed, and other
LSP clients.

![csls dashboard showing a live C# diagnostic](../../assets/screenshots/dashboard.svg)

The dashboard shows the active workspace, diagnostics, requests, caches, logs,
and traces. Click the screenshot to view it at full size.

[Install csls](./getting-started/) or [configure an editor](./editors/).
