---
title: Development
description: Build, test, and verify csls changes.
---

## Install the SDK

The repository requires the exact .NET SDK pinned by `global.json`. Install the
SDK before running any `dotnet` command from the repository directory.

- **Windows:** install the .NET 10 SDK with
  `winget install --id Microsoft.DotNet.SDK.10 --version 10.0.400 --exact`, or use the official
  [Windows installer](https://learn.microsoft.com/dotnet/core/install/windows).
- **Linux:** install `dotnet-sdk-10.0` using the supported package instructions
  for your distribution in [Install .NET on Linux](https://learn.microsoft.com/dotnet/core/install/linux).
- **macOS:** use the .NET 10 SDK package from
  [Install .NET on macOS](https://learn.microsoft.com/dotnet/core/install/macos).

Select SDK version 10.0.400 on every platform; installing only another .NET 10
feature band does not satisfy the repository's deterministic SDK pin.

If another .NET 10 SDK is already available, install the pinned SDK into the
repository's `.dotnet` directory without administrator access. Run the file app
from the repository's parent directory so the missing pinned SDK does not block
the command.

PowerShell:

```powershell
$repo = (Get-Location).Path
Set-Location ..
dotnet run --file "$repo/scripts/InstallDotNet.cs"
Set-Location $repo
```

Linux or macOS shell:

```sh
repo="$PWD"
cd ..
dotnet run --file "$repo/scripts/InstallDotNet.cs"
cd "$repo"
```

Return to the repository and verify `dotnet --version` reports the version in
`global.json`.

## Build and test

Run tests through Microsoft Testing Platform:

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
