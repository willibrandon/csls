# csls

`csls` is a C# language server, command-line interface, and agent platform for
.NET developers. It targets .NET 10 and is designed for terminal editors, IDEs,
automation, and AI agents.

Windows, Linux, and macOS on x64 and arm64 are first-class build and test
targets. Real Fresh, GNU Emacs/Eglot, Helix, and Neovim sessions exercise the
server through Hex1b terminal automation.

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
