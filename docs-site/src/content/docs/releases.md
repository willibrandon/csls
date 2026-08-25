---
title: Releases
description: Install and verify csls release packages and standalone builds.
---

`csls` and `csls-mcp` use the same release version. The .NET tool packages select
the implementation for the current runtime automatically.

```console
dotnet tool install --global csls
dotnet tool install --global csls-mcp
```

## Runtime packages

Native AOT packages are built for Windows, Linux, and macOS on x64 and Arm64. Linux
musl is built on x64 and Arm64. Windows x86 uses the supported self-contained
ReadyToRun implementation. An `any` package provides the framework-dependent .NET
10 fallback.

Each `csls` implementation contains the launcher, server worker, and CLI worker.
Each `csls-mcp` implementation contains the MCP launcher, MCP worker, and transient
server worker. Package manifests, command runners, bundled worker paths, licenses,
readmes, and forbidden build files are inspected before execution.
Release validation also rejects outdated, deprecated, or vulnerable package
references and retains the NuGet reports with the workflow run.

## Release gate

A release candidate must pass:

1. repository policy, formatting, analyzers, and all MSTest projects;
2. real Fresh, Emacs, Helix, Neovim, VS Code, and Zed sessions;
3. package install, version, help, worker handshake, update, and uninstall checks;
4. every runtime package build and Native AOT size budget;
5. BenchmarkDotNet validation and cross-platform end-to-end measurements;
6. CodeQL, Picket repository scan, dev-container image scan, and docs link validation.

Run the package verifier with a release candidate version before publishing:

```console
dotnet run --file scripts/Verify-ToolPackages.cs -- --version 1.0.0-rc.1
```

The generated package directory is temporary validation output. Release packages
must be built from the protected `main` commit that receives the version tag.

## Standalone archives

Each release includes a standalone archive and a separate symbol archive for every
supported runtime. The standalone archive contains the launcher and its managed
workers. Extract it as one directory and keep the worker layout intact.

Homebrew formulas, Scoop manifests, WinGet manifests, and Nix expressions are built
from the hashes of those archives. The same release also publishes multi-platform
`ghcr.io/willibrandon/csls` and `ghcr.io/willibrandon/csls-mcp` container images.

## Verify a download

Download `SHA256SUMS` with the selected archive and compare its SHA-256 digest before
running it. The release also contains an SPDX 2.2 SBOM. GitHub provenance and SBOM
attestations cover the published files, while the container registry stores separate
provenance and SBOM records for each image.
