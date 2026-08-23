# Repository scripts

The scripts are .NET file-based apps. Run them from the repository root with
`dotnet run --file scripts/<name>.cs`. Use `--` before script arguments. Each app
supports `--help`.

| Script | Purpose |
| --- | --- |
| `Initialize-DevContainer.cs` | Installs the development container dependencies and restores the repository. |
| `InstallDotNet.cs` | Installs the pinned .NET SDK from verified Microsoft release metadata. |
| `Install-NativeAotPrerequisites.cs` | Installs the native compiler prerequisites for a runtime identifier. |
| `Provision-Actionlint.cs` | Installs the pinned GitHub Actions validator. |
| `Provision-CsharpLsOracle.cs` | Installs the pinned language server parity oracle. |
| `Provision-Emacs.cs` | Installs the pinned Emacs Eglot editor oracle. |
| `Provision-Fresh.cs` | Installs the pinned Fresh editor oracle. |
| `Provision-Helix.cs` | Installs the pinned Helix editor oracle. |
| `Provision-Neovim.cs` | Installs the pinned Neovim editor oracle. |
| `Run-Benchmarks.cs` | Builds and runs the BenchmarkDotNet suite. |
| `Verify-Repository.cs` | Checks repository, dependency, and automation policies. |
| `Verify-GitHubActions.cs` | Validates every workflow with actionlint. |
| `Verify-CodeQl.cs` | Fails when CodeQL reports an unresolved finding. |
| `Verify-ToolPackages.cs` | Packs and exercises the `csls` and `csls-mcp` tools. |
| `Export-ContainerImage.cs` | Exports a container image as a Picket-scannable archive. |

`ScriptSupport.cs` contains shared download, checksum, archive, process, and path
helpers used by the provisioning apps.
