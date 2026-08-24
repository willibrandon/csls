---
title: Configuration
description: Configure csls through standard Language Server Protocol settings.
---

The preferred configuration section is `csls`. The compatible `csharp` section
is also accepted. When both sections define a setting, `csls` takes precedence.

```json
{
  "csls": {
    "enableAnalyzers": true,
    "formatOnSave": false
  }
}
```

`enableAnalyzers` defaults to `true`. Disable it to return compiler diagnostics
without running analyzers referenced by the loaded project.

`formatOnSave` defaults to `false`. Set it to `true` when the client should request
server formatting edits before saving C#, Razor, or cshtml files.

Clients that advertise `workspace.configuration` are queried after initialization
and whenever they send `workspace/didChangeConfiguration`. Other clients can push
the same settings in that notification. Configuration changes invalidate affected
diagnostic results before later requests run.

## Workspace folders

`csls` accepts multiple folders during initialization and advertises workspace
folder change notifications. Added folders are loaded through the same solution,
project, and loose-file discovery used at startup. Removed folders are unloaded.
Unsaved documents in folders that remain loaded keep their current text and version.
