---
title: Getting started
description: Build csls and start the language server.
---

The .NET 10 SDK version is pinned in `global.json`. Install that SDK, clone the
repository, then build and test it:

```console
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
```

If the pinned SDK is not installed, the repository includes a cross-platform C#
file app that installs it locally:

```console
dotnet run --file scripts/InstallDotNet.cs
```

Run the language server from a source checkout with:

```console
dotnet run --project src/Csls.App -- lsp
```

The default `csls` command and the explicit `csls lsp` command both serve LSP
over standard input and output. Editors should use `csls lsp` because the intent
is visible in their configuration.

The `csls` and `csls-mcp` .NET tool packages are prepared for Windows, Linux,
and macOS on x64 and Arm64. Linux musl and Windows x86 packages are also built
and verified in CI.

Install both tools when an MCP client will use the language server:

```console
dotnet tool install --global csls
dotnet tool install --global csls-mcp
csls agent mcp --workspace .
```
