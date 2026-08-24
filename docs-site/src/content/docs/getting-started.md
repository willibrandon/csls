---
title: Getting started
description: Install csls and start the language server.
---

Install the language server as a global .NET tool:

```console
dotnet tool install --global csls
```

Start the language server with:

```console
csls lsp
```

The default `csls` command and the explicit `csls lsp` command both serve LSP
over standard input and output. Editors should use `csls lsp` because the intent
is visible in their configuration.

## File-based apps

Open a file-based C# app directly or open its containing directory. csls uses
the selected .NET SDK and honors `#:package`, `#:project`, `#:include`,
`#:property`, and `#:sdk` directives.

The `csls` and `csls-mcp` .NET tool packages are prepared for Windows, Linux,
and macOS on x64 and Arm64. Linux musl and Windows x86 packages are also built
and verified in CI.

Install the separate MCP tool when an MCP client will use the language server:

```console
dotnet tool install --global csls-mcp
csls agent mcp --workspace .
```
