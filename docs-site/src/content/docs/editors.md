---
title: Editors
description: Connect Fresh, Helix, Neovim, Emacs, VS Code, Zed, and other LSP clients to csls.
---

Configure the server command as `csls` with `lsp` as its only argument. Start the
editor from a directory containing a solution or project so Roslyn can load the
workspace.

## Fresh

Add a C# language server entry to `fresh.json`:

```json
{
  "lsp": {
    "csharp": {
      "command": "csls",
      "args": ["lsp"],
      "enabled": true,
      "auto_start": true,
      "root_markers": [".slnx", ".sln", ".csproj", ".git"]
    }
  }
}
```

Fresh only starts language servers in a trusted workspace.

## Helix

Add this to `languages.toml`:

```toml
[language-server.csls]
command = "csls"
args = ["lsp"]

[[language]]
name = "c-sharp"
language-servers = ["csls"]
```

Run `hx --health c-sharp` if Helix cannot find the command.

![Helix showing Roslyn hover information from csls](../../assets/screenshots/helix-hover.svg)

Click the screenshot to view it at full size. It is captured from the same real
Helix and Hex1b session used by the integration test.

## Neovim

Neovim 0.11 and later can register `csls` directly:

```lua
vim.lsp.config("csls", {
  cmd = { "csls", "lsp" },
  filetypes = { "cs" },
  root_markers = { "*.slnx", "*.sln", "*.csproj", ".git" },
})
vim.lsp.enable("csls")
```

## GNU Emacs

Register `csls` with Eglot before opening a C# buffer:

```elisp
(add-to-list 'eglot-server-programs
             '((csharp-mode csharp-ts-mode) . ("csls" "lsp")))
```

Run `M-x eglot` if the current C# mode does not start Eglot automatically.

## Zed

Install the `csls` extension from Zed's extension gallery. It downloads the
matching csls release for the current operating system and architecture, verifies
the published checksum, and starts the `csls` language server for C# and Razor.

Use a local build while developing the server:

```json
{
  "code_lens": "on",
  "languages": {
    "CSharp": {
      "language_servers": ["csls", "!roslyn", "!omnisharp", "!csharp-ls"]
    }
  },
  "lsp": {
    "csls": {
      "binary": {
        "path": "csls",
        "arguments": ["lsp"]
      }
    }
  }
}
```

`code_lens` can also be set to `menu` to put reference counts in the code-action
menu instead of above declarations. Selecting a reference lens opens Zed's native
location view.

## VS Code

Install the `willibrandon.csls` extension and disable the Microsoft C# and C# Dev
Kit extensions so one language client owns each C# document. Desktop and remote
extension hosts run the packaged Native AOT launcher and Roslyn worker. The .NET
Install Tool supplies the supported runtime and SDK. The Solution view supports
restore, build, run, debug, and Microsoft Testing Platform tests.
Reference counts appear above supported C# declarations and open VS Code's native
references popup when selected.

VS Code for the Web runs csls in a WebAssembly worker and synchronizes the virtual
workspace without requiring a local .NET installation. Language features and the
Solution view work in the browser. Commands that start local processes remain on
desktop and remote workspace hosts.

The repository runs one feature contract against desktop, remote, Chromium,
Firefox, and WebKit extension hosts. The contract covers hover, completion,
definition, reference CodeLens, semantic tokens, configurable inlay hints,
diagnostics after edits, formatting, rename, code actions, created files, and
server restart.

## Other clients

Any other LSP client that can launch a standard input and output server can run:

```console
csls lsp
```

Use the workspace folder URI during `initialize`. The server discovers `.slnx`,
`.sln`, `.csproj`, and file-based app entry points below that folder. Multi-root
clients can add and remove folders without restarting the server.
