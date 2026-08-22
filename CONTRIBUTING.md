# Contributing

Use the SDK pinned by `global.json` and run:

```console
dotnet restore Csls.slnx
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
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
