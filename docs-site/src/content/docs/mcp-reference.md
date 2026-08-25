---
title: MCP reference
description: Generated tools, resources, and prompts from the live csls MCP server.
---

This page is generated through the official MCP client from a real csls session.

## Tools

| Tool | Behavior | Inputs | Description |
| --- | --- | --- | --- |
| `apply_edit_plan` | Destructive | `planId` required | Apply one previewed csls edit plan after generation, version, and SHA-256 checks pass. |
| `cancel_request` | Destructive | `correlationId` required | Cancel one queued or running csls request by its stable correlation identifier. |
| `clear_caches` | Destructive | None | Clear retained diagnostic, semantic-token, and pending-edit cache entries for the attached session. |
| `get_code_actions` | Read only | `documentPath` required, `startLine` required, `startCharacter` required, `endLine` required, `endCharacter` required, `kind` required | Get concrete Roslyn code actions for a zero-based UTF-16 source range. |
| `get_completion` | Read only | `documentPath` required, `line` required, `character` required | Get bounded C# completion candidates and exact commit edits at a zero-based UTF-16 position. |
| `get_declaration` | Read only | `documentPath` required, `line` required, `character` required | Find source declarations for the C# symbol at a zero-based UTF-16 document position. |
| `get_definition` | Read only | `documentPath` required, `line` required, `character` required | Find source definitions for the C# symbol at a zero-based UTF-16 document position. |
| `get_diagnostics` | Read only | `documentPath` required, `previousResultId` | Get current compiler and analyzer diagnostics for one document in the attached csls session. |
| `get_document_highlights` | Read only | `documentPath` required, `line` required, `character` required | Get semantic read, write, and declaration occurrences within one C# document. |
| `get_document_symbols` | Read only | `documentPath` required | Get the hierarchical C# declarations in one document from the attached csls session. |
| `get_hover` | Read only | `documentPath` required, `line` required, `character` required | Resolve C# signature and documentation hover information at a zero-based UTF-16 document position. |
| `get_implementation` | Read only | `documentPath` required, `line` required, `character` required | Find source implementations for the C# symbol at a zero-based UTF-16 document position. |
| `get_references` | Read only | `documentPath` required, `line` required, `character` required, `includeDeclaration` required | Find source references for the C# symbol at a zero-based UTF-16 document position. |
| `get_selection_range` | Read only | `documentPath` required, `line` required, `character` required | Get the nested C# syntax selection at a zero-based UTF-16 document position. |
| `get_session` | Read only | None | Get lifecycle, workspace generation, roots, and process details for the attached csls session. |
| `get_signature_help` | Read only | `documentPath` required, `line` required, `character` required | Get overload-aware C# signature help at a zero-based UTF-16 document position. |
| `get_type_definition` | Read only | `documentPath` required, `line` required, `character` required | Find source definitions for the type of the C# symbol at a zero-based UTF-16 position. |
| `get_workspace_state` | Read only | `includeDiagnostics` required | Get bounded workspace, project, document, request, build-host, cache, log, and optional diagnostic state. |
| `list_requests` | Read only | None | List bounded queued and running requests with correlation identifiers and current trace state. |
| `preview_formatting` | Read only | `documentPath` required, `tabSize` required, `insertSpaces` required | Preview complete-document Roslyn formatting edits without applying them. |
| `preview_rename` | Read only | `documentPath` required, `line` required, `character` required, `newName` required | Preview a semantic C# rename as version-aware workspace edits without applying it. |
| `reload_workspace` | Mutating | None | Atomically reload every workspace root while preserving unsaved open document overlays. |
| `restart_build_hosts` | Mutating | None | Recreate every Roslyn workspace host while preserving unsaved open document overlays. |
| `restore_workspace` | Mutating | None | Run dotnet restore for every workspace entry point and atomically reload Roslyn state while preserving open overlays. |
| `search_workspace_symbols` | Read only | `query` required | Search bounded C# source declarations across the attached csls workspace. |
| `start_trace` | Mutating | None | Start one bounded request lifecycle trace for the attached csls session. |
| `stop_trace` | Mutating | None | Stop the active request lifecycle trace and return its bounded retained entries. |

## Resources

| URI | Name | Description |
| --- | --- | --- |
| `csls://session/current` | Current csls session | Current lifecycle, workspace generation, roots, and process details for the attached csls session. |
| `csls://workspace/current` | Current csls workspace | Current workspaces, projects, documents, requests, build hosts, caches, and recent logs for the attached session. |

## Resource templates

| URI template | Name | Description |
| --- | --- | --- |
| `csls://diagnostic{?path}` | csls document diagnostics | Current compiler and analyzer diagnostics for one loaded C# document. |
| `csls://document{?path}` | csls document | One loaded C# document selected by its absolute source file path. |
| `csls://project{?path}` | csls project | One loaded Roslyn project selected by its absolute project file path. |

## Prompts

| Prompt | Description |
| --- | --- |
| `diagnose_csharp` | Investigate C# diagnostics with csls and require a verified root cause before proposing changes. |
| `explain_symbol` | Explain a C# symbol using csls hover, definition, reference, and project context. |
| `refactor_csharp` | Plan and apply a C# refactoring through csls with version preconditions and verification. |
| `review_csharp` | Review C# code using csls semantic evidence and actionable findings. |
| `troubleshoot_csls` | Troubleshoot a csls or editor integration symptom from session state, queues, logs, and workspace evidence. |
