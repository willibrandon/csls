# Repository scripts

The scripts are .NET file-based apps. Run them from the repository root with
`dotnet run --file scripts/<name>.cs`. Use `--` before script arguments. Each app
supports `--help`.

| Script | Purpose |
| --- | --- |
| `Capture-Docs.cs` | Captures verified terminal screenshots and rebuilds the documentation site. |
| `Assemble-ReleaseAssets.cs` | Assembles the runtime matrix, package-manager metadata, SPDX SBOM, and checksums. |
| `Build-ReleaseAssets.cs` | Builds and verifies packages, standalone archives, symbols, and container inputs for one runtime. |
| `Build-VsCodeExtension.cs` | Packages a native-platform or web VS Code extension with its matching csls server. |
| `Build-VsCodeWebWorker.cs` | Builds the browser WebAssembly language-server worker used by the VS Code web extension. |
| `Build-ZedExtension.cs` | Builds the WebAssembly extension package consumed by Zed. |
| `Export-ContainerImage.cs` | Exports a container image as a Picket-scannable archive. |
| `Generate-Docs.cs` | Generates CLI, MCP, configuration, and contract reference pages from the built product. |
| `Initialize-DevContainer.cs` | Installs the development container dependencies and restores the repository. |
| `InstallDotNet.cs` | Installs the pinned SDK locally from verified Microsoft release metadata; when that SDK is missing, invoke it by absolute path from outside the repository using another .NET 10 SDK. |
| `Install-GraphicalEditorTestPrerequisites.cs` | Installs Linux display and input packages for graphical editor tests. |
| `Install-NativeAotPrerequisites.cs` | Installs the native compiler prerequisites for a runtime identifier. |
| `Provision-Actionlint.cs` | Installs the pinned GitHub Actions validator. |
| `Provision-CsharpLsOracle.cs` | Installs the pinned language server parity oracle. |
| `Provision-Emacs.cs` | Installs the pinned Emacs Eglot editor oracle. |
| `Provision-Fresh.cs` | Installs the pinned Fresh editor oracle. |
| `Provision-Helix.cs` | Installs the pinned Helix editor oracle. |
| `Provision-LegacyBuildHost.cs` | Installs Mono MSBuild or verifies Visual Studio MSBuild for legacy workspaces. |
| `Provision-Neovim.cs` | Installs the pinned Neovim editor oracle. |
| `Provision-SbomTool.cs` | Installs the verified Microsoft SBOM Tool release. |
| `Provision-VsCode.cs` | Installs the pinned VS Code test client, builds its host fixtures, and optionally installs its web browsers. |
| `Provision-VsCodeRemoteServer.cs` | Installs the matching VS Code server used by remote extension-host tests. |
| `Provision-WasiSdk.cs` | Installs the verified WASI SDK used to compile Zed grammar modules. |
| `Provision-Zed.cs` | Installs the pinned Zed editor and C# extension oracle. |
| `Prepare-Release.cs` | Validates a release version and Git tag and writes workflow outputs. |
| `Publish-ContainerTags.cs` | Advances stable container tags to verified image digests. |
| `Publish-GitHubRelease.cs` | Creates or updates a tagged GitHub release with verified assets. |
| `Publish-NuGet.cs` | Publishes the complete NuGet package set with a short-lived credential. |
| `Publish-VsCodeExtension.cs` | Publishes the verified platform and web VSIX set to Visual Studio Marketplace and Open VSX. |
| `Run-Benchmarks.cs` | Builds and runs the BenchmarkDotNet suite. |
| `Run-EndToEndPerformance.cs` | Publishes both Native AOT tools and measures real LSP, MCP, CLI, dashboard, and process-resource workloads. |
| `Select-DevContainerValidation.cs` | Selects full container validation when a pull request changes its inputs. |
| `Verify-Repository.cs` | Checks repository, dependency, and automation policies. |
| `Verify-ReleaseContainers.cs` | Builds, executes, and exports both release container images. |
| `Verify-GitHubActions.cs` | Validates every workflow with actionlint. |
| `Verify-PackageHealth.cs` | Rejects outdated, deprecated, and vulnerable package references before release. |
| `Verify-CodeQl.cs` | Fails on product-source CodeQL findings while identifying known findings in Microsoft-generated serializer code. |
| `Verify-Docs.cs` | Checks generated documentation links, assets, and accessibility conditions. |
| `Verify-BenchmarkRegression.cs` | Compares stable benchmarks affected by a change and confirms signals with longer targeted measurements before rejecting regressions above 10 percent. |
| `Verify-FileApps.cs` | Compiles every file app and verifies its help boundary. |
| `Verify-ToolPackages.cs` | Packs and exercises the `csls` and `csls-mcp` tools. |

`ScriptSupport.cs` contains shared download, checksum, archive, process, and path
helpers used by the provisioning apps.
