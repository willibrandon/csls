---
title: Troubleshooting
description: Diagnose installation, SDK, workspace, protocol, Razor, and session failures.
---

Start with the workspace doctor from the same directory or path the editor opens:

```console
csls doctor .
csls doctor . --json
csls doctor . --binlog artifacts/csls-doctor.binlog
```

The report separates target discovery, SDK selection, language-server startup,
workspace load, source diagnostics, and optional build failure. Language services
remain available for a loaded project with source errors. Resolve SDK and
project-load failures using the corresponding report section.

## Check editor startup

Run `csls --version` and `csls lsp --help` in the editor's environment. If the
command is missing, inspect the .NET global tool path or reinstall the tool. Editor
configuration should use `csls` with `lsp` as its argument. Standard output carries
LSP frames; diagnostics go to standard error.

## Check workspace loading

Pass the workspace directory, solution, project, or source file to `csls doctor`.
SDK selection follows that target directory and its `global.json`.

For SDK projects, confirm that `dotnet --version` succeeds from the workspace. For a
legacy .NET Framework project, install a compatible Visual Studio or Build Tools
MSBuild on Windows. On Unix, install a Mono distribution that includes MSBuild when
the project requires the .NET Framework build host. Roslyn logs a clear fallback
when only the current .NET SDK host is available.

Repository contributors can install or verify the expected host with
`sudo dotnet run --file scripts/Provision-LegacyBuildHost.cs` on Linux or
`dotnet run --file scripts/Provision-LegacyBuildHost.cs` on macOS and Windows.

File-based apps must use a selected SDK that understands their directives. Run the
file directly with `dotnet run --file` if package, project, include, property, or SDK
evaluation fails before csls opens it.

## Check Razor project configuration

Confirm that the Razor file belongs to a loaded project and that its project restores.
Razor views and components use the generated project snapshot, imports, references,
and current unsaved source.

Use `csls doctor --binlog` when generated Razor references or SDK imports differ from
the command line. Check both source diagnostics and workspace logs in the dashboard.

## Find a live session

Run:

```console
csls sessions list --json
```

The control socket is local to the current operating-system user. Run discovery
in the editor's user and container environment. Session discovery verifies that
each socket belongs to a live process.

If one editor has several workspaces, select the session with `--workspace`. If
several sessions own the same path, select the exact process with `--session`.

## Select an MCP target

Register the MCP server as `csls-mcp`. Pass exactly one flat selector in each
target-dependent language-service tool or resource request. Use `workspace` with an existing
directory, solution, project, or document path, `session` with a positive process
identifier from `list_sessions`, or `socket` with an absolute live control-socket
path.

If a workspace matches several editor sessions, select the intended process or
socket explicitly. After a target disconnects, repeat a workspace-selected request
to resolve the current live session or start a new transient one. Correct selector
errors by submitting a new request on the same MCP connection.

## A request appears stuck

Open `csls dashboard`, inspect Requests, then start a bounded trace. Each request has
a correlation identifier, current phase, mode, workspace generation, duration, and
cancellation state. Use `csls requests cancel` only for the matching live identifier.

Protocol clients can request `$/csharp/debugInfo` to inspect the queue and workspace
phase while a foreground request is blocked. This request runs independently of
the foreground scheduler.
A notification that appears in statistics with an unexpectedly short duration often
failed before its intended work completed; standard error contains the server log.

## Package or Native AOT failure

Run the package verifier for the host runtime. It builds the manifest, native runtime
package, and framework-dependent fallback, then installs and exercises both tools.

```console
dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- --runtime linux-x64
dotnet run --file scripts/Verify-ToolPackages.cs
```

Native AOT requires the platform compiler and development libraries installed by the
prerequisite app. Package source mapping must allow the local validation packages and
the Microsoft runtime host package selected by `dotnet tool install`.

For repository build failures, keep the MSBuild binary log and the TRX test artifacts.
They preserve evaluated imports, SDK resolution, target ordering, test names, and
the first concrete failure.
