---
title: MCP reference
description: Generated multi-workspace tools, resource templates, and prompts from csls-mcp.
---

This page is generated through the official MCP client from the bare csls MCP server. Start `csls-mcp` without arguments. Every target-dependent tool and resource requires exactly one of `workspace`, `session`, or `socket`. Target selectors are shown separately from operation-specific inputs.

## Tools

| Tool | Behavior | Target | Operation inputs | Description |
| --- | --- | --- | --- | --- |
| `apply_edit_plan` | Destructive | Exactly one of `workspace`, `session`, or `socket` | `planId` required | Apply one previewed csls edit plan after generation, version, and SHA-256 checks pass. |
| `cancel_request` | Destructive | Exactly one of `workspace`, `session`, or `socket` | `correlationId` required | Cancel one queued or running request in one selected csls session. |
| `clear_caches` | Destructive | Exactly one of `workspace`, `session`, or `socket` | None | Clear retained result caches for one explicitly selected csls session. |
| `get_code_actions` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `startLine` required, `startCharacter` required, `endLine` required, `endCharacter` required, `kind` required | Get concrete Roslyn code actions for a zero-based UTF-16 source range. |
| `get_completion` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Get bounded C# completion candidates and exact commit edits at a zero-based UTF-16 position. |
| `get_declaration` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Find source declarations for the C# symbol at a zero-based UTF-16 document position. |
| `get_definition` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Find source definitions for the C# symbol at a zero-based UTF-16 document position. |
| `get_diagnostics` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `previousResultId` | Get current compiler and analyzer diagnostics for one document in the selected csls session. |
| `get_document_highlights` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Get semantic read, write, and declaration occurrences within one C# document. |
| `get_document_symbols` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required | Get the hierarchical C# declarations in one document from the selected csls session. |
| `get_hover` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Resolve C# signature and documentation hover information at a zero-based UTF-16 document position. |
| `get_implementation` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Find source implementations for the C# symbol at a zero-based UTF-16 document position. |
| `get_references` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required, `includeDeclaration` required | Find source references for the C# symbol at a zero-based UTF-16 document position. |
| `get_selection_range` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Get the nested C# syntax selection at a zero-based UTF-16 document position. |
| `get_session` | Read only | Exactly one of `workspace`, `session`, or `socket` | None | Get lifecycle, workspace generation, roots, and process details for the selected csls session. |
| `get_signature_help` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Get overload-aware C# signature help at a zero-based UTF-16 document position. |
| `get_type_definition` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required | Find source definitions for the type of the C# symbol at a zero-based UTF-16 position. |
| `get_workspace_state` | Read only | Exactly one of `workspace`, `session`, or `socket` | None | Get a compact workspace health overview. Follow the returned resource link only when complete project, document, request, cache, log, or diagnostic details are needed. |
| `list_requests` | Read only | Exactly one of `workspace`, `session`, or `socket` | None | List bounded queued and running requests for one selected csls session. |
| `list_sessions` | Read only | None | None | List responsive csls sessions and their process, socket, lifecycle, and workspace identities. |
| `preview_formatting` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `tabSize` required, `insertSpaces` required | Preview complete-document Roslyn formatting edits without applying them. |
| `preview_rename` | Read only | Exactly one of `workspace`, `session`, or `socket` | `documentPath` required, `line` required, `character` required, `newName` required | Preview a semantic C# rename as version-aware workspace edits without applying it. |
| `reload_workspace` | Mutating | Exactly one of `workspace`, `session`, or `socket` | None | Atomically reload every root in one selected workspace while preserving unsaved overlays. |
| `restart_build_hosts` | Mutating | Exactly one of `workspace`, `session`, or `socket` | None | Recreate every Roslyn host in one selected workspace while preserving unsaved overlays. |
| `restore_workspace` | Mutating | Exactly one of `workspace`, `session`, or `socket` | None | Run dotnet restore for every workspace entry point and atomically reload one selected Roslyn workspace. |
| `search_workspace_symbols` | Read only | Exactly one of `workspace`, `session`, or `socket` | `query` required | Search bounded C# source declarations across the selected csls workspace. |
| `start_trace` | Mutating | Exactly one of `workspace`, `session`, or `socket` | None | Start one bounded request lifecycle trace for one selected csls session. |
| `stop_trace` | Mutating | Exactly one of `workspace`, `session`, or `socket` | None | Stop the active request lifecycle trace for one selected csls session. |

## Resources

csls exposes target-selected state only through the resource templates below.

## Resource templates

| URI template | Name | Description |
| --- | --- | --- |
| `csls://diagnostic/{?workspace,session,socket,path}` | csls document diagnostics | Current compiler and analyzer diagnostics for one selected loaded C# document. |
| `csls://document/{?workspace,session,socket,path}` | csls document | One loaded C# document selected by target and absolute source file path. |
| `csls://project/{?workspace,session,socket,path}` | csls project | One loaded Roslyn project selected by target and absolute project file path. |
| `csls://session/{?workspace,session,socket}` | csls session | Lifecycle, workspace generation, roots, and process details for one selected csls session. |
| `csls://workspace/{?workspace,session,socket}` | csls workspace | Workspaces, projects, documents, requests, hosts, caches, and logs for one selected session. |

## Prompts

| Prompt | Description |
| --- | --- |
| `diagnose_csharp` | Investigate C# diagnostics with csls and require a verified root cause before proposing changes. |
| `explain_symbol` | Explain a C# symbol using csls hover, definition, reference, and project context. |
| `refactor_csharp` | Plan and apply a C# refactoring through csls with version preconditions and verification. |
| `review_csharp` | Review C# code using csls semantic evidence and actionable findings. |
| `troubleshoot_csls` | Troubleshoot a csls or editor integration symptom from session state, queues, logs, and workspace evidence. |
