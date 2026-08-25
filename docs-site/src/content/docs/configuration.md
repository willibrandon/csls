---
title: Configuration
description: Configure csls through standard Language Server Protocol settings.
---

The preferred configuration section is `csls`. The compatible `csharp` section
is also accepted. When both sections define a setting, `csls` takes precedence.
The [configuration reference](../configuration-reference/) is generated from the
server contract and records the current defaults.

```json
{
  "csls": {
    "enableAnalyzers": true,
    "formatOnSave": false,
    "configuration": "Debug",
    "logLevel": "Information"
  }
}
```

`enableAnalyzers` defaults to `true`. Disable it to return compiler diagnostics
without running analyzers referenced by the loaded project.

`formatOnSave` defaults to `false`. Set it to `true` when the client should request
server formatting edits before saving C#, Razor, or cshtml files.

`configuration` defaults to `Debug`. Changing it reloads each MSBuild workspace
with the selected configuration while preserving open document text and versions.

`logLevel` defaults to `Information` and accepts the Microsoft logging levels from
`Trace` through `Critical`, plus `None`. Changes apply to both standard error and
the session log exposed to the dashboard, CLI, and MCP server.

Clients that advertise `workspace.configuration` are queried after initialization
and whenever they send `workspace/didChangeConfiguration`. Other clients can push
the same settings in that notification. Configuration changes reload or invalidate
only the state affected by the new values before later requests run.

## Workspace folders

`csls` accepts multiple folders during initialization and advertises workspace
folder change notifications. Added folders are loaded through the same solution,
project, and loose-file discovery used at startup. Removed folders are unloaded.
Unsaved documents in folders that remain loaded keep their current text and version.
