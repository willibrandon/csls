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
workspace load, source diagnostics, and optional build failure. A source error does
not prevent language service, but SDK or project-load failure does.

## The editor cannot start csls

Run `csls --version` and `csls lsp --help` in the editor's environment. If the
command is missing, inspect the .NET global tool path or reinstall the tool. Editor
configuration should use `csls` with `lsp` as its only argument. Do not add logging
arguments that write to standard output because that stream is reserved for LSP.

## No solution or project loads

Pass the workspace directory, solution, project, or source file to `csls doctor`.
SDK selection follows that target directory and its `global.json`. The launch
directory is not used as a substitute for the workspace path.

For SDK projects, confirm that `dotnet --version` succeeds from the workspace. For a
legacy .NET Framework project, install a compatible Visual Studio or Build Tools
MSBuild on Windows. On Unix, install a Mono distribution that includes MSBuild when
the project requires the .NET Framework build host. Roslyn logs a clear fallback
when only the current .NET SDK host is available.

Repository contributors can install or verify the expected host with
`dotnet run --file scripts/Provision-LegacyBuildHost.cs`.

File-based apps must use a selected SDK that understands their directives. Run the
file directly with `dotnet run --file` if package, project, include, property, or SDK
evaluation fails before csls opens it.

## Razor results are missing

Confirm that the Razor file belongs to a loaded project and that its project restores.
Razor views and components use the generated project snapshot, imports, references,
and current unsaved source. A loose Razor file without an owning project cannot
provide project-aware C# semantics.

Use `csls doctor --binlog` when generated Razor references or SDK imports differ from
the command line. Check both source diagnostics and workspace logs in the dashboard.

## A session is not discoverable

Run:

```console
csls sessions list --json
```

The control socket is local to the current operating-system user. Containers,
elevated processes, and different user accounts do not share its per-user temporary
directory. A stale socket is ignored when its owner process is no longer live.

If one editor has several workspaces, select the session with `--workspace`. If
several sessions own the same path, select the exact process with `--session`.

## A request appears stuck

Open `csls dashboard`, inspect Requests, then start a bounded trace. Each request has
a correlation identifier, current phase, mode, workspace generation, duration, and
cancellation state. Use `csls requests cancel` only for the matching live identifier.

Protocol clients can request `$/csharp/debugInfo`. It bypasses normal scheduling, so
it still reports the queue and workspace phase while a foreground request is blocked.
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
They contain evaluated imports, SDK resolution, target ordering, test names, and the
first concrete failure without relying on console truncation.
