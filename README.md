# csls

`csls` is a C# language server, command-line interface, and agent platform for
.NET developers. It targets .NET 10 and is designed for terminal editors, IDEs,
automation, and AI agents.

The repository is under active development toward its complete 1.0 feature set.
No unimplemented LSP capability is advertised by the server.

## Build

```console
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
```

The exact SDK is pinned in `global.json`. Install it locally with the file-based app:

```console
dotnet run --file scripts/InstallDotNet.cs
```
