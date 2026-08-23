---
title: Dashboard
description: Inspect live language-server state in a Hex1b terminal interface.
---

Start `csls` in an editor, then attach the dashboard to the active session:

```console
csls dashboard
```

Pass `--session` when more than one language server is running:

```console
csls dashboard --session 12345
```

You can also select the session that owns a workspace or document:

```console
csls dashboard --workspace src/MyApp/MyApp.csproj
```

The dashboard reads the same versioned control service used by the CLI and MCP
server. It shows sessions, workspaces, projects, documents, diagnostics,
requests, queues, build hosts, caches, and bounded structured logs.

Use the arrow keys to select a view. Press <kbd>F5</kbd> to refresh and
<kbd>Ctrl</kbd>+<kbd>C</kbd> to exit. Diagnostics are evaluated when their view
is selected so normal attachment does not compile every loaded project.

The Actions view can restore or reload a workspace, restart its Roslyn build
hosts, and clear retained results. Every mutation opens a confirmation window.
The same actions are available from <kbd>F6</kbd> through <kbd>F9</kbd>.
Reload and host restart preserve unsaved document text owned by the editor.
