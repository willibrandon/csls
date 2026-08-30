# Contributing

Install the SDK pinned by `global.json` before running any repository command.
Windows, Linux, macOS, and local non-admin installation instructions are at
<https://willibrandon.github.io/csls/development/#install-the-sdk>.

Then run:

```console
dotnet restore Csls.slnx
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
dotnet run --file scripts/Verify-Repository.cs
dotnet run --file scripts/Verify-ToolPackages.cs
```

Tests use MSTest 4 on Microsoft.Testing.Platform. Always run `dotnet test` and
never use `--no-build`. Product tests exercise real processes, streams, sockets,
files, workspaces, SDKs, and editor integrations. Mocking libraries and hand-written
substitutes for production services are prohibited.

Each C# file contains one type. Every public or internal type and member has
triple-slash XML documentation, and each `<summary>` uses exactly three lines:
an opening tag, one text line, and a closing tag.

Repository automation is implemented only as .NET file-based C# apps under
`scripts/`. Shell, PowerShell, batch, and command scripts are not used.

The GitHub Actions matrix runs the complete suite on x64 and arm64 Windows,
Linux, and macOS runners. It also validates the Windows x86, Linux musl x64,
and Linux musl arm64 tool packages. Every Native AOT launcher is checked from
its ILC size report by Dotsider, and CodeQL findings fail the analysis job. The
development container installs every required editor oracle and build dependency
through
`scripts/Initialize-DevContainer.cs`; its exported image is scanned by Picket.

Provision the real editor and parity oracles locally with:

```console
dotnet run --file scripts/Provision-CsharpLsOracle.cs
dotnet run --file scripts/Provision-Fresh.cs
dotnet run --file scripts/Provision-Emacs.cs
dotnet run --file scripts/Provision-Helix.cs
dotnet run --file scripts/Provision-Neovim.cs
```

Set `CSLS_TOOLS_ROOT` to keep provisioned tools outside the repository. The
development container uses a container-local tool root so prefix-dependent
editor installations never reuse artifacts from the host checkout. Its build
artifacts use an isolated volume so container restores cannot overwrite host
MSBuild and NuGet state.

The container also installs Node.js 24.19.0 and Rust 1.98.0 with rust-analyzer,
rustfmt, Clippy, Rust sources, and the `wasm32-wasip2` target. Its post-create app
restores the VS Code packages and the locked Zed crate graph.

Build the editor extensions with:

```console
npm --prefix editors/vscode run compile
cargo clippy --locked --manifest-path editors/zed/Cargo.toml --all-targets --all-features -- -D warnings
cargo build --locked --release --target wasm32-wasip2 --manifest-path editors/zed/Cargo.toml
dotnet run --file scripts/Build-ZedExtension.cs
```
