---
title: Architecture
description: Understand the csls process model, project boundaries, and workspace state.
---

csls keeps distribution, compiler work, protocol contracts, and user interfaces in
separate assemblies. The small launchers can use Native AOT while Roslyn and MSBuild
remain in managed workers where dynamic project loading is available.

## Process model

```text
editor A ── LSP stdio ── csls launcher ── server worker A ── Roslyn/MSBuild
                                                 └── control socket A ──┬── CLI / dashboard
                                                                      └──┐
workspace B ── MCP-owned transient server B ────── control socket B ─────┤
                                                                         ▼
MCP client ── stdio ── csls-mcp launcher ── MCP worker / session broker
```

The `csls` launcher supervises the server worker and gives it the inherited standard
streams. The worker owns the LSP connection, request scheduler, Roslyn workspaces,
and control socket. When the editor closes the protocol streams or sends the normal
shutdown sequence, the launcher waits for that worker to exit.

CLI commands start a separate managed CLI worker. The worker attaches to the
selected server session through its private control socket. The dashboard runs in
that same worker and uses Hex1b for terminal rendering. `csls-mcp` uses its own
Native AOT launcher and managed MCP worker. A shared session broker resolves a
typed selector per call, reuses live control connections, and owns bounded
transient sessions for workspaces that have no live editor session.

The broker retains target connections for the MCP connection lifetime so edit
plans, traces, and workspace generations stay affiliated with the selected
language server. It admits at most 32 MCP-owned transient sessions and 256 total
cached sessions; exceeding either limit rejects the new acquisition without
evicting active state. A disconnected target is evicted independently and is not
retried or retargeted during the failed operation. Disconnecting the MCP client
cancels outstanding calls, stops owned transient workers, and only closes control
connections to editor-owned workers.

## Projects

| Project | Responsibility |
| --- | --- |
| `Csls.App` | Native AOT `csls` launcher and command routing |
| `Csls.Worker` | Managed language-server host and dependency injection |
| `Csls.Server` | LSP handlers and capability negotiation |
| `Csls.Workspaces` | SDK discovery, Roslyn workspaces, Razor mapping, and semantic edits |
| `Csls.Core` | Bounded request scheduling, cancellation, tracing, and shared primitives |
| `Csls.Protocol` | LSP payloads and source-generated JSON metadata |
| `Csls.Rpc` | StreamJsonRpc registration, framing, and client callbacks |
| `Csls.Control.Contracts` | Versioned local control requests and responses |
| `Csls.Control` | Session discovery and local control transport |
| `Csls.Control.Server` | Dashboard state, maintenance, queries, and guarded edit plans |
| `Csls.Client` | Language-server client used by transient CLI and MCP sessions |
| `Csls.Cli.Worker` | Session, query, edit, doctor, and dashboard commands |
| `Csls.Dashboard` | Hex1b terminal interface |
| `Csls.Mcp` | Native AOT `csls-mcp` launcher |
| `Csls.Mcp.Worker` | Official C# MCP SDK tools, resources, and prompts |
| `Csls.SourceGen` | Repository structure and documentation analyzers |

Project references point toward contracts and compiler services. The protocol and
control contract projects do not depend on UI or process hosts. This keeps wire
types reusable and prevents editor behavior from leaking into the workspace layer.

## Workspace state

Each configured folder owns a Roslyn workspace and solution. A load, reload, or
topology change builds a new set of folders before publishing it as one immutable
generation. Read requests capture that generation. Mutations wait for earlier reads,
publish their new generation, then release later work.

Open documents are versioned overlays on the current solution. Reloads preserve
those overlays for folders that remain active. Diagnostics, semantic tokens, and
guarded edit plans record the generation and document version they were computed
from, so stale results are not applied to newer source.

The request scheduler bounds queued work and separates concurrent reads, exclusive
mutations, and background reads. Cancellation remains attached to the original
request correlation identifier from admission through Roslyn execution.

## Distribution boundary

The launchers contain command parsing and process supervision only. Native AOT keeps
tool startup small without forcing Roslyn, MSBuild, Razor, editor integration, or MCP
reflection into the native image. Managed workers are bundled inside each runtime
package and are started with the selected .NET host.

See [RPC and control](../rpc-and-control/) for the two wire protocols and
[dependencies](../dependencies/) for the package boundary.
