---
title: Dependencies
description: See why each product dependency exists and how versions are maintained.
---

NuGet versions are pinned once in `Directory.Packages.props` through Central Package
Management. Projects declare package names without repeating versions. The repository
does not generate `packages.lock.json` files; CI restores against the central pins
and tests the resulting tools on every supported runtime.

## Product packages

| Package family | Used for |
| --- | --- |
| Microsoft.CodeAnalysis | C# syntax, compiler services, workspaces, features, and SDK, Build Tools, or Mono build-host selection |
| Microsoft.CodeAnalysis.Razor.Compiler and Microsoft.AspNetCore.Razor.Utilities.Shared | Razor project compilation and generated C# mappings |
| Microsoft.Build and Microsoft.Build.Locator | Project evaluation and workspace-selected .NET SDK registration |
| Microsoft.Extensions | Worker hosting, dependency injection, and structured logging |
| StreamJsonRpc | LSP and local control JSON-RPC transports |
| System.CommandLine | `csls` and `csls-mcp` command parsing |
| ModelContextProtocol | Official C# MCP server, tools, resources, and prompts |
| Hex1b | Dashboard widgets, terminal rendering, and terminal integration tests |

Framework libraries cover JSON, sockets, cryptography, process control, archives,
and file access. Product code does not add a third-party abstraction when the .NET
runtime or a Microsoft package already provides the required API. Hex1b is the
intentional terminal UI dependency.

Roslyn analyzer, Source Link, MSBuild task, and runtime host packages used only while
building are private assets. They do not become public package dependencies of the
installed tools.

## Tool layout

The `csls` and `csls-mcp` manifest packages select an implementation package for the
host runtime identifier. A runtime implementation contains the Native AOT launcher
and its managed workers. The `any` implementation is a framework-dependent fallback.
Roslyn and MSBuild stay in the worker output rather than the Native AOT image.

## Development-only packages

MSTest and Microsoft Testing Platform run the test projects. BenchmarkDotNet is used
only by the microbenchmark project. Documentation, provisioning, and repository
verification may use packages that are not shipped with either tool. The product
dependency restriction does not apply to those development programs, but every
version is still pinned and reviewed.

Visual Studio Build Tools on Windows and Mono MSBuild on Unix are optional runtime
requirements for old-style .NET Framework projects. They are CI and development
dependencies, not files redistributed by either csls tool package.

Dependabot checks NuGet, npm, and GitHub Actions weekly. A dependency update must pass
the full runtime matrix, Native AOT publishing, real editor tests, CodeQL, Picket,
formatting, repository policy, and package install, update, execution, and uninstall
verification before it can reach `main`.
