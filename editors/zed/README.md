# csls for Zed

The csls extension provides C# and Razor language support through Zed's native
language server integration. It uses a `csls` command from the worktree when one
is available. Otherwise it downloads the matching stable release and verifies
its published SHA-256 checksum before installation.

Use `lsp.csls.binary` in Zed settings to select a local build. Settings under
`lsp.csls.settings` are sent to the `csls` configuration section.

```json
{
  "code_lens": "on",
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

Set `code_lens` to `menu` to show reference counts in the code-action menu
instead of above declarations. Selecting a count opens Zed's native location
view.

The extension also registers the `.NET (csls)` debug adapter and runs the same
downloaded or configured `csls` binary with `debugger dap`. A `debug.json` launch
entry names an absolute managed executable or assembly:

```json
[
  {
    "label": ".NET Launch",
    "adapter": "csls",
    "request": "launch",
    "program": "/absolute/path/to/application.dll",
    "cwd": "/absolute/path/to/project"
  }
]
```

For attachment, set `request` to `attach` and provide a positive `processId`.
The extension uses the csls debugger directly and never downloads or discovers
another debugger.

Build the extension with `dotnet run --file scripts/Build-ZedExtension.cs` from
the repository root.
