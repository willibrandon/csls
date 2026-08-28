# csls for Zed

The csls extension provides C# and Razor language support through Zed's native
language server integration. It uses a `csls` command from the worktree when one
is available. Otherwise it downloads the matching stable release and verifies
its published SHA-256 checksum before installation.

Use `lsp.csls.binary` in Zed settings to select a local build. Settings under
`lsp.csls.settings` are sent to the `csls` configuration section.

```json
{
  "lsp": {
    "csls": {
      "binary": {
        "path": "/absolute/path/to/csls",
        "arguments": ["lsp"]
      },
      "settings": {
        "enableAnalyzers": true,
        "configuration": "Debug"
      }
    }
  }
}
```

Build the extension with `dotnet run --file scripts/Build-ZedExtension.cs` from
the repository root.
