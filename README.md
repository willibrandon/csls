# csls

`csls` is a Native AOT C# language server, command-line interface, and agent
platform for .NET developers. It targets .NET 10 and is designed for terminal
editors, IDEs, automation, and AI agents.

Windows, Linux, and macOS on x64 and arm64 are first-class build and test
targets. Real Fresh, GNU Emacs/Eglot, Helix, and Neovim sessions exercise the
server through Hex1b terminal automation.

The repository is under active development toward its complete 1.0 feature set.
No unimplemented LSP capability is advertised by the server.

Read the [csls documentation](https://willibrandon.github.io/csls/) for editor,
CLI, MCP, and development guidance.

Run `csls dashboard` while a language-server session is active to inspect its
workspaces, projects, documents, diagnostics, requests, caches, and logs. The
CLI and dashboard can also restore, reload, restart build hosts, and clear caches
through the same live control service.

Run `csls-mcp --workspace <path>` for an agent-owned workspace or attach it to a
live editor session with `--session <pid>`.

## Build

```console
dotnet build Csls.slnx
dotnet test --solution Csls.slnx
```

The exact SDK is pinned in `global.json`. Install it locally with the file-based app:

```console
dotnet run --file scripts/InstallDotNet.cs
```
