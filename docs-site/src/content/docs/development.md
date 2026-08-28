---
title: Development
description: Build, test, and verify csls changes.
---

Use the pinned .NET 10 SDK and run tests through Microsoft Testing Platform:

```console
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
dotnet format Csls.slnx --verify-no-changes
```

Tests run methods in parallel. They use real Roslyn workspaces, real LSP and MCP
transports, and installed editor processes. Fresh, GNU Emacs with Eglot, Helix,
and Neovim run inside Hex1b terminals so synchronization follows visible terminal
state instead of fixed delays. VS Code runs the same feature contract in desktop,
remote, Chromium, Firefox, and WebKit extension hosts. Zed runs with the csls
extension under a real display server.

Repository automation is implemented as .NET file apps under `scripts/`:

```console
dotnet run --file scripts/Verify-Repository.cs
dotnet run --file scripts/Verify-GitHubActions.cs
dotnet run --file scripts/Verify-ToolPackages.cs
dotnet run --file scripts/Build-ReleaseAssets.cs -- --help
```

The development container restores every file app and provisions its external
test tools at pinned current releases, including Mono MSBuild for old project files.
CI builds the supported runtime packages, verifies Visual Studio and Mono build
hosts, runs CodeQL, scans the repository and container image with Picket, and checks
the Native AOT package sizes.

Read [testing](../testing/) for the real process and editor fixtures,
[performance](../performance/) for both measurement programs, and
[architecture](../architecture/) before changing project boundaries.

Build the documentation with:

```console
npm ci --prefix docs-site
npm run build --prefix docs-site
```

Regenerate the verified terminal screenshots and rebuild the site with:

```console
dotnet run --file scripts/Capture-Docs.cs
```
