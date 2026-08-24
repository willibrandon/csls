---
title: Releases
description: Prepare, verify, and publish the csls and csls-mcp .NET tools.
---

`csls` and `csls-mcp` are versioned and published separately. A release uses the same
version for each manifest package and all of its runtime implementation packages.
The tool manifest lets `dotnet tool install` choose the matching host package.

## Runtime packages

Native AOT packages are built for Windows, Linux, and macOS on x64 and Arm64. Linux
musl is built on x64 and Arm64. Windows x86 uses the supported self-contained
ReadyToRun implementation. An `any` package provides the framework-dependent .NET
10 fallback.

Each `csls` implementation contains the launcher, server worker, and CLI worker.
Each `csls-mcp` implementation contains the MCP launcher, MCP worker, and transient
server worker. Package manifests, command runners, bundled worker paths, licenses,
readmes, and forbidden build files are inspected before execution.

## Release gate

A release candidate must pass:

1. repository policy, formatting, analyzers, and all MSTest projects;
2. real Fresh, Emacs, Helix, and Neovim sessions;
3. package install, version, help, worker handshake, update, and uninstall checks;
4. every runtime package build and Native AOT size budget;
5. BenchmarkDotNet validation and cross-platform end-to-end measurements;
6. CodeQL, Picket repository scan, dev-container image scan, and docs link validation.

Run the package verifier with a release candidate version before publishing:

```console
dotnet run --file scripts/Verify-ToolPackages.cs -- --version 0.1.0-rc.1
```

The generated package directory is temporary validation output. Release packages
must be built from the protected `main` commit that receives the version tag.

## NuGet publishing

NuGet trusted publishing is the required publication mechanism. The GitHub
environment and NuGet policy must restrict the repository, workflow, package owner,
and release environment. The workflow exchanges GitHub's short-lived OIDC identity
for a NuGet token only during the publish job. No long-lived NuGet API key belongs in
repository or environment secrets.

Trusted publishing is enabled after the complete release gate and package identity
are stable. The first publication also verifies ownership of both package IDs before
automation is allowed to create a public release.

After publication, install both tools into an empty tool directory and run their
version and help commands from the public NuGet source. The release is complete only
when those packages select and execute the expected runtime implementation.
