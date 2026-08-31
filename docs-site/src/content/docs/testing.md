---
title: Testing
description: Run the real workspace, protocol, editor, package, and policy tests.
---

csls uses MSTest on Microsoft Testing Platform. Test methods run in parallel, while
fixtures that launch costly external programs use bounded shared leases. Run tests
without suppressing the build step so source and analyzer errors cannot hide behind
an older test binary.

```console
dotnet test --solution Csls.slnx
dotnet test --project tests/Csls.Tests/Csls.Tests.csproj -- --filter "Name~Completion"
```

## Real behavior

Language-server tests start the real managed worker and drive production standard
streams with StreamJsonRpc. They create temporary SDK projects, solutions, Razor
projects, file-based apps, Unity layouts, and legacy project shapes, then assert on
Roslyn results and protocol payloads. MCP tests use the official client and server
transport. Control tests use real Unix domain sockets.

Multi-target coverage loads an SDK project with several target frameworks and
verifies the selected Roslyn flavor through a real hover request. Diagnostic cache
coverage changes one project and confirms that an unrelated project stays unchanged.

Tests do not replace Roslyn, MSBuild, the file system, processes, clocks, sockets, or
edit application with mocking libraries. Malformed wire data is constructed only
when the behavior under test is input rejection.

The language suite covers advertised capabilities, initialization, cancellation,
workspace generations, file operations, diagnostics, semantic edits, CLI commands,
MCP tools, control resources, package workers, and shutdown. Parity cases keep
ported behavior executable without publishing private backlog references.
Watched-file coverage changes a closed source file on disk through the real LSP
worker, observes the diagnostic refresh request, and verifies that an open dependent
document updates without restarting the editor.

## Editor sessions

Fresh, GNU Emacs with Eglot, Helix, and Neovim run in real Hex1b terminals. The
packaged VS Code extension runs in desktop and remote extension hosts. Its browser
extension and WebAssembly server run in Chromium, Firefox, and WebKit. Every VS Code
host executes the same hover, completion, definition, semantic token, configurable
inlay hint, diagnostics, formatting, rename, code action, file synchronization, and
restart contract. Zed runs with the csls extension.
Tests wait for visible editor or protocol state instead of fixed delays.

Provisioners are .NET file-based apps. Each one selects a compatible release
for the host operating system and architecture, verifies its digest, extracts it
under `artifacts/tools`, and reuses it on later runs.

```console
dotnet run --file scripts/Provision-Fresh.cs
dotnet run --file scripts/Provision-Emacs.cs
dotnet run --file scripts/Provision-Helix.cs
dotnet run --file scripts/Provision-Neovim.cs
dotnet run --file scripts/Provision-VsCode.cs
dotnet run --file scripts/Provision-VsCodeRemoteServer.cs
dotnet run --file scripts/Provision-Zed.cs
```

Pass `--with-web-browsers` to the VS Code provisioner to install the matching
Chromium, Firefox, and WebKit builds. Linux also needs the browser and display
packages installed by `Install-GraphicalEditorTestPrerequisites.cs`.

Legacy workspace jobs run old project files without reference-assembly packages.
Windows uses the Visual Studio or Build Tools MSBuild host, while Linux and macOS
use Mono MSBuild. The test fails unless framework references and semantic results
come from the platform host. Run its prerequisite with:

```console
# Linux
sudo dotnet run --file scripts/Provision-LegacyBuildHost.cs

# macOS and Windows
dotnet run --file scripts/Provision-LegacyBuildHost.cs
```

## Debugging a failing protocol test

`$/csharp/debugInfo` is the first diagnostic source. A phase of `Uninitialized`
means initialization did not complete. Empty folders mean no workspace was loaded.
Request statistics show whether a notification entered and completed its handler.

Server logs are written to standard error. A focused test can forward that stream
while it runs, but temporary diagnostic output must not remain in product code. Use
the correlation identifier from debug information, the dashboard, or a trace to
follow cancellation and scheduling.

## Repository gates

The test matrix covers Windows, Linux, macOS, x64, and Arm64. Dedicated jobs verify
Visual Studio Build Tools and Mono project loading. Additional package jobs cover
Windows x86 and Linux musl. The dev-container job builds and scans the same container
developers use. Repository policy rejects warning suppressions, ignored tests,
missing XML documentation, multiple types in one file, fixed workflow action versions,
and dependencies outside the approved product boundary.

Test results are written as TRX artifacts. MSBuild failures should be rerun with a
binary logger so evaluation, SDK selection, and project imports can be inspected.
