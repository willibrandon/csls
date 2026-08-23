---
title: Editors
description: Connect Fresh, Helix, Neovim, Emacs, and other LSP clients to csls.
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

## Other clients

Any LSP client that can launch a standard input and output server can run:

```console
csls lsp
```

Use the workspace folder URI during `initialize`. The server discovers `.slnx`,
`.sln`, and `.csproj` files below that folder.
