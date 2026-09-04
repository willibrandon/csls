---
title: MCP reference
description: Generated multi-workspace tools, resource templates, and prompts from csls-mcp.
---

This page is generated through the official MCP client from the complete csls MCP installation. Start `csls-mcp` without arguments. Language-service operations select exactly one `workspace`, `session`, or `socket`; debugger operations use their explicit `debugSession`. Target selectors are shown separately from operation-specific inputs.

## Tools

| Tool | Behavior | Target | Operation inputs | Description |
| --- | --- | --- | --- | --- |
| `apply_edit_plan` | Destructive | Exactly one of `workspace`, `session`, or `socket` | `planId` required | Apply one previewed csls edit plan after generation, version, and SHA-256 checks pass. |
| `cancel_request` | Destructive | Exactly one of `workspace`, `session`, or `socket` | `correlationId` required | Cancel one queued or running request in one selected csls session. |
| `clear_caches` | Destructive | Exactly one of `workspace`, `session`, or `socket` | None | Clear retained result caches for one explicitly selected csls session. |
| `debug_agent_control_set` | Mutating | `debugSession` required | `enabled` required, `durationSeconds` | Grant or revoke time-bounded target-changing authority for one explicit debugSession. Grants are connection-local and never inherited. |
| `debug_breakpoints_get` | Read only | `debugSession` required | None | Get every authoritative breakpoint and exception policy for one explicit debugger session. |
| `debug_disassemble` | Read only | `debugSession` required | `stopGeneration` required, `instructionReference` required, `instructionCount` required, `byteOffset`, `instructionOffset`, `resolveSymbols` | Disassemble up to 256 managed-IL instructions from a generation-bound location. |
| `debug_evaluate` | Read only | `debugSession` required | `stopGeneration` required, `frameId` required, `expression` required | Evaluate a source-language-aware side-effect-free expression without executing target code. |
| `debug_exception_breakpoints_set` | Destructive | `debugSession` required | `stopGeneration` required, `breakpoints` required | Replace the managed-exception policy at an exact stopped generation. Requires an active debug_agent_control_set grant. |
| `debug_exception_get` | Read only | `debugSession` required | `stopGeneration` required, `threadId` required | Get the managed exception responsible for one thread's exact stopped generation. |
| `debug_execute_expression` | Destructive | `debugSession` required | `stopGeneration` required, `frameId` required, `expression` required | Execute an explicitly qualified instance or loaded-type static method in the target. Requires an active debug_agent_control_set grant and the exact stopGeneration; the method may have arbitrary side effects. |
| `debug_execution_control` | Destructive | `debugSession` required | `operation` required, `stopGeneration`, `threadId`, `stepKind`, `targetId` | Pause, continue, or source-step one explicit debugger session. Requires an active debug_agent_control_set grant; continue and step also require the current stopGeneration. |
| `debug_expression_set` | Destructive | `debugSession` required | `stopGeneration` required, `frameId` required, `expression` required, `value` required | Set a writable local, argument, instance field, or array element. String, call, and construction results may resume the target to materialize the value and return a newer stopGeneration. Requires an active debug_agent_control_set grant and the exact stopGeneration. |
| `debug_function_breakpoints_set` | Destructive | `debugSession` required | `stopGeneration` required, `breakpoints` required | Replace all managed-function breakpoints at an exact stopped generation. Requires an active debug_agent_control_set grant. |
| `debug_goto` | Destructive | `debugSession` required | `stopGeneration` required, `threadId` required, `targetId` required | Move a managed thread to a runtime-approved debug_goto_targets_get destination. Requires an active debug_agent_control_set grant and the exact stopGeneration. |
| `debug_goto_targets_get` | Read only | `debugSession` required | `stopGeneration` required, `frameId` required, `sourcePath` required, `line` required, `column` | Get runtime-approved go-to destinations for one source position in the exact stopped generation. |
| `debug_hot_reload` | Destructive | `debugSession` required | `stopGeneration` required, `moduleId` required, `expectedModuleGeneration` required, `metadataDeltaBase64` required, `ilDeltaBase64` required, `pdbDeltaBase64` required, `updatedTypes` required, `requiredCapabilities` required, `updatedMethods` required, `activeStatements` required | Apply compiler-produced metadata, IL, and Portable PDB deltas to one Hot Reload-enabled module. Requires an active debug_agent_control_set grant and exact stop and module generations. |
| `debug_instruction_breakpoints_set` | Destructive | `debugSession` required | `stopGeneration` required, `breakpoints` required | Replace all managed-IL instruction breakpoints at an exact stopped generation. Requires an active debug_agent_control_set grant. |
| `debug_memory_read` | Read only | `debugSession` required | `stopGeneration` required, `memoryReference` required, `count` required, `offset` | Read up to 65536 target bytes from a generation-bound memory reference. |
| `debug_modules_get` | Read only | `debugSession` required | `startModule`, `moduleCount` | Get a bounded page of managed modules and validated symbol status for one explicit debugger session. |
| `debug_output_get` | Read only | `debugSession` required | `afterSequence`, `count` | Get a bounded retained stdout/stderr page after a stable session-local sequence. |
| `debug_scopes_get` | Read only | `debugSession` required | `stopGeneration` required, `frameId` required | Get arguments, locals, and other scopes for one frame in the current stopGeneration. |
| `debug_session_attach` | Destructive | None | `processId` required, `pause`, `sourceFileMap` | Attach an isolated debugger worker to one explicit managed process and return its debugSession identifier. |
| `debug_session_end` | Destructive | `debugSession` required | `terminateAttachedTarget` | End one explicit debugger session. Launched targets terminate; attached targets detach unless terminateAttachedTarget is explicitly requested. |
| `debug_session_get` | Read only | `debugSession` required | None | Get current state for exactly one debugSession; no active target is inferred. |
| `debug_session_restart` | Destructive | `debugSession` required | `stopGeneration` required | Restart one stopped debugger target with its original launch or attach request. Requires an active debug_agent_control_set grant and the exact stopGeneration. |
| `debug_session_start` | Destructive | None | `program` required, `workingDirectory` required, `arguments`, `environment`, `runtimeHostPath`, `sourceFileMap`, `initialSourcePath`, `initialLine`, `suppressJitOptimizations`, `enableHotReload`, `justMyCode`, `enableStepFiltering` | Launch one managed .NET target in an isolated debugger worker and return its explicit debugSession identifier. |
| `debug_sessions_list` | Read only | None | None | List only the explicit debugger sessions owned by this MCP connection. |
| `debug_source_breakpoints_set` | Destructive | `debugSession` required | `stopGeneration` required, `sourcePath` required, `breakpoints` required | Replace all source breakpoints for one absolute document at an exact stopped generation. Requires an active debug_agent_control_set grant. |
| `debug_source_get` | Read only | `debugSession` required | `stopGeneration` required, `sourceReference` required, `start`, `count` | Get a bounded source-text page from one generation-bound source reference. |
| `debug_stack_get` | Read only | `debugSession` required | `stopGeneration` required, `threadId` required, `startFrame`, `levels` | Get a bounded managed stack page for one thread in the current stopGeneration. |
| `debug_step_targets_get` | Read only | `debugSession` required | `stopGeneration` required, `frameId` required | Get source-aware Step Into calls for one frame in the exact stopped generation. |
| `debug_threads_get` | Read only | `debugSession` required | `stopGeneration` required | Get managed threads for one explicit debugger session and current stopGeneration. |
| `debug_variable_set` | Destructive | `debugSession` required | `stopGeneration` required, `variablesReference` required, `name` required, `value` required | Set one child of a current-generation variable container. String, call, and construction results may resume the target to materialize the value and return a newer stopGeneration. Requires an active debug_agent_control_set grant and the exact stopGeneration. |
| `debug_variables_get` | Read only | `debugSession` required | `stopGeneration` required, `variablesReference` required, `start`, `count` | Get a bounded child page from one variable container in the current stopGeneration. |
| `debug_variables_get_presented` | Destructive | `debugSession` required | `stopGeneration` required, `variablesReference` required, `start`, `count` | Get debugger-presented child variables, including DebuggerTypeProxy views. Requires an active debug_agent_control_set grant and the exact stopGeneration because proxy constructors may execute arbitrary target code. |
| `debug_watches_get` | Read only | `debugSession` required | `stopGeneration` required, `frameId` required, `expressions` required | Evaluate one through 64 independent side-effect-free watches without executing target code. |
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
| `csls://debug/breakpoints/{debugSession}` | csls debugger breakpoints | Every authoritative breakpoint and exception policy for one debugger session. |
| `csls://debug/disassembly/{debugSession}/{stopGeneration}{?instructionReference,byteOffset,instructionOffset,instructionCount,resolveSymbols}` | csls debugger disassembly | Bounded managed-IL instructions for one reference and stopped generation. |
| `csls://debug/exception/{debugSession}/{stopGeneration}/{threadId}` | csls debugger exception | Managed exception detail for one thread and stopped generation. |
| `csls://debug/memory/{debugSession}/{stopGeneration}{?memoryReference,offset,count}` | csls debugger memory | Bounded target memory for one opaque reference and stopped generation. |
| `csls://debug/modules/{debugSession}{?startModule,moduleCount}` | csls debugger modules | Bounded managed modules and validated symbol status for one debugger session. |
| `csls://debug/output/{debugSession}{?afterSequence,count}` | csls debugger output | Bounded stdout/stderr page after a stable debugger-session sequence cursor. |
| `csls://debug/scopes/{debugSession}/{stopGeneration}/{frameId}` | csls debugger scopes | Managed scopes for one frame and stopped generation. |
| `csls://debug/session/{debugSession}` | csls debugger session | Current lifecycle and stop-generation state for one explicit debugger session. |
| `csls://debug/source/{debugSession}/{stopGeneration}/{sourceReference}{?start,count}` | csls debugger source | Bounded source text for one reference and stopped generation. |
| `csls://debug/stack/{debugSession}/{stopGeneration}/{threadId}{?startFrame,levels}` | csls debugger stack | Bounded managed stack page for one thread and stopped generation. |
| `csls://debug/threads/{debugSession}/{stopGeneration}` | csls debugger threads | Managed threads for one explicit debugger stopped generation. |
| `csls://debug/variables/{debugSession}/{stopGeneration}/{variablesReference}{?start,count}` | csls debugger variables | Bounded variables for one container and stopped generation. |
| `csls://debug/watches/{debugSession}/{stopGeneration}/{frameId}{?expression}` | csls debugger watch | One independently evaluated watch for a frame and stopped generation. |
| `csls://diagnostic/{?workspace,session,socket,path}` | csls document diagnostics | Current compiler and analyzer diagnostics for one selected loaded C# document. |
| `csls://document/{?workspace,session,socket,path}` | csls document | One loaded C# document selected by target and absolute source file path. |
| `csls://project/{?workspace,session,socket,path}` | csls project | One loaded Roslyn project selected by target and absolute project file path. |
| `csls://session/{?workspace,session,socket}` | csls session | Lifecycle, workspace generation, roots, and process details for one selected csls session. |
| `csls://workspace/{?workspace,session,socket}` | csls workspace | Workspaces, projects, documents, requests, hosts, caches, and logs for one selected session. |

## Prompts

| Prompt | Description |
| --- | --- |
| `diagnose_csharp` | Investigate C# diagnostics with csls and require a verified root cause before proposing changes. |
| `diagnose_dotnet_debugger_failure` | Diagnose a .NET debugger failure from explicit session state and bounded evidence. |
| `explain_dotnet_debugger_state` | Explain one explicit .NET debugger session from generation-consistent evidence. |
| `explain_symbol` | Explain a C# symbol using csls hover, definition, reference, and project context. |
| `plan_dotnet_breakpoints` | Plan .NET breakpoints from source and debugger evidence without changing the target. |
| `refactor_csharp` | Plan and apply a C# refactoring through csls with version preconditions and verification. |
| `review_csharp` | Review C# code using csls semantic evidence and actionable findings. |
| `triage_dotnet_dump` | Triage one explicit read-only .NET process-dump session from bounded evidence. |
| `troubleshoot_csls` | Troubleshoot a csls or editor integration symptom from session state, queues, logs, and workspace evidence. |
