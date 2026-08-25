---
title: RPC and control
description: Follow LSP, StreamJsonRpc, scheduling, and the private control protocol.
---

csls uses two StreamJsonRpc connections with separate contracts and framing. LSP is
the editor-facing protocol on standard input and output. The control protocol is a
versioned local API for the CLI, dashboard, and MCP server.

## LSP connection

The server uses standard `Content-Length` header framing over its inherited streams.
`LspMethodRegistry` registers supported requests and notifications explicitly. The
initialize result advertises only handlers that are active in the current build.
Inbound header blocks are limited to 8 KiB and declared JSON payloads are limited to
16 MiB. The worker rejects either limit before StreamJsonRpc allocates the payload.

Payloads use System.Text.Json with source-generated metadata from `Csls.Protocol`
and `Csls.Rpc`. Document URIs, discriminated LSP response shapes, and protocol enums
therefore have one shared serializer configuration on both sides of the connection.
No transport code writes diagnostic text to standard output; logs go to standard
error so the JSON-RPC stream stays valid.

The server registers client callbacks only when negotiation requires them. These
include configuration pulls, progress, capability registration, diagnostics, edit
application, and refresh requests.

## Request scheduling

Every language operation declares one scheduler mode:

| Mode | Behavior |
| --- | --- |
| `ReadOnly` | Runs concurrently against one immutable workspace generation |
| `ReadWrite` | Waits for prior reads and runs exclusively while publishing state |
| `ReadOnlyBackground` | Uses a separate bounded concurrency limit for background work |

The queue has fixed capacity and preserves mutation order. Each accepted request has
a stable correlation identifier, receive ordinal, cancellation token, phase, and
workspace generation. The dashboard and CLI expose this state without changing the
editor protocol.

`$/csharp/debugInfo` bypasses normal scheduling. It reports workspace phase, folder
counts, current requests, queue counters, and cumulative timings even when a normal
request is blocked. Tests use it for state-based synchronization and failure
diagnosis.

## Control connection

Each server worker creates one Unix domain socket under `.csls/sockets` in the
current user profile. .NET provides this socket family on Windows, Linux, and macOS.
csls does not open a TCP listener.

On Unix, the directory is restricted to its owner and rejected if it is a symbolic
link. The bound socket is readable and writable only by that owner. Windows applies
a protected directory ACL for the current user. Session discovery also checks process
identity before treating a socket as live.

Control messages use a four-byte big-endian length followed by JSON. The server
rejects nonpositive payloads and payloads larger than 4 MiB before deserialization.
Connection admission is bounded, and each connection registers only the methods in
`IControlRpcTarget`.

Method names include the control protocol version, for example
`csls/control/v1/session/get`. Responses use source-generated JSON contracts. A
future incompatible shape requires a new version instead of changing an existing
method in place.

## Ownership and shutdown

An attached CLI or MCP process never owns the editor's server. Closing its control
connection leaves the server running. Direct MCP and doctor sessions own their
transient worker and complete the normal LSP shutdown handshake when finished.

Editor cancellation reaches the scheduled Roslyn operation. Control cancellation
uses the same correlation identifier and token. Shutdown stops admission, drains
accepted scheduler work, closes control connections, removes the socket, and then
lets the launcher return the worker exit code.
