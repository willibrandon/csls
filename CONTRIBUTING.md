# Contributing

Use the SDK pinned by `global.json` and run:

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
and Linux musl arm64 tool packages. The development container installs every
required editor oracle and build dependency through
`scripts/Initialize-DevContainer.cs`; its exported image is scanned by Picket.

Provision the real editor and parity oracles locally with:

```console
dotnet run --file scripts/Provision-CsharpLsOracle.cs
dotnet run --file scripts/Provision-Fresh.cs
dotnet run --file scripts/Provision-Emacs.cs
dotnet run --file scripts/Provision-Helix.cs
dotnet run --file scripts/Provision-Neovim.cs
```
