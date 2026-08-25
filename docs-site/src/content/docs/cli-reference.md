---
title: CLI reference
description: Generated System.CommandLine help for every csls command.
---

This page is generated from the command tree compiled into `csls`.

## csls

```text
Description:
  Fast C# language intelligence for editors, terminals, and agents.

Usage:
  csls [command] [options]

Options:
  -?, -h, --help  Show help and usage information
  --version       Show version information

Commands:
  lsp            Run the Language Server Protocol over standard I/O.
  sessions       Inspect live csls language-server sessions.
  dashboard      Inspect language-server state in the Hex1b dashboard.
  doctor <path>  Inspect SDK selection and load the workspace through a transient csls session. [default: .]
  workspace      Maintain workspaces through a csls session.
  requests       Inspect and cancel requests in a csls session.
  trace          Control bounded request lifecycle tracing.
  query          Query language intelligence from a csls session.
  edit           Preview semantic workspace edits through a csls session.
  agent          Connect coding agents to csls through MCP and reusable instructions.
```

## csls lsp

```text
Description:
  Run the Language Server Protocol over standard I/O.

Usage:
  csls lsp [options]

Options:
  -?, -h, --help  Show help and usage information
```

## csls sessions

```text
Description:
  Inspect live csls language-server sessions.

Usage:
  csls sessions [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  list   List every live csls session.
  show   Show one live csls session.
  watch  Watch live csls sessions until canceled.
```

## csls dashboard

```text
Description:
  Inspect language-server state in the Hex1b dashboard.

Usage:
  csls dashboard [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  -?, -h, --help      Show help and usage information
```

## csls doctor

```text
Description:
  Inspect SDK selection and load the workspace through a transient csls session.

Usage:
  csls doctor [<path>] [options]

Arguments:
  <path>  Workspace directory, solution, project, or C# document path. [default: .]

Options:
  --json           Write the versioned machine-readable response envelope.
  --binlog <path>  Build the workspace and write an MSBuild binary log to this path.
  -?, -h, --help   Show help and usage information
```

## csls workspace

```text
Description:
  Maintain workspaces through a csls session.

Usage:
  csls workspace [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  restore             Restore loaded solution and project entry points, then reload the workspace.
  reload              Reload the workspace while preserving unsaved document overlays.
  restart-build-host  Recreate Roslyn workspace hosts while preserving unsaved document overlays.
  clear-cache         Clear retained diagnostic, semantic-token, and pending-edit results.
```

## csls requests

```text
Description:
  Inspect and cancel requests in a csls session.

Usage:
  csls requests [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  list                     List queued and running requests.
  cancel <correlation-id>  Cancel one queued or running request.
```

## csls trace

```text
Description:
  Control bounded request lifecycle tracing.

Usage:
  csls trace [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  start  Start request lifecycle tracing for a session.
  stop   Stop request lifecycle tracing and return its entries.
```

## csls query

```text
Description:
  Query language intelligence from a csls session.

Usage:
  csls query [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  hover <document>             Resolve hover information at a document position.
  diagnostics <document>       Get compiler and analyzer diagnostics for one document.
  completion <document>        Get bounded Roslyn completion candidates at one document position.
  definition <document>        Find source definitions for the symbol at one document position.
  declaration <document>       Find source declarations for the symbol at one document position.
  type-definition <document>   Find source definitions for the symbol's type.
  implementation <document>    Find source implementations for the symbol at one document position.
  selection-range <document>   Get the nested syntax selection at one document position.
  highlights <document>        Get semantic symbol occurrences within one document.
  references <document>        Find source references for the symbol at one document position.
  document-symbols <document>  Get the hierarchical declarations in one document.
  symbols <pattern>            Search source declarations across the current workspace.
  signature-help <document>    Get overload-aware signature help at one document position.
```

## csls edit

```text
Description:
  Preview semantic workspace edits through a csls session.

Usage:
  csls edit [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  rename <document> <new-name>  Preview a semantic cross-document rename.
  format <document>             Preview complete-document Roslyn formatting.
  code-action <document>        Preview concrete Roslyn code actions.
```

## csls agent

```text
Description:
  Connect coding agents to csls through MCP and reusable instructions.

Usage:
  csls agent [command] [options]

Options:
  -?, -h, --help  Show help and usage information

Commands:
  mcp   Launch the separately installed csls MCP server.
  init  Create a reusable csls agent skill file.
```

## csls sessions list

```text
Description:
  List every live csls session.

Usage:
  csls sessions list [options]

Options:
  --cursor <cursor>  Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>    Maximum number of result items from 1 through 200. [default: 100]
  --json             Write the versioned machine-readable response envelope.
  -?, -h, --help     Show help and usage information
```

## csls sessions show

```text
Description:
  Show one live csls session.

Usage:
  csls sessions show [options]

Options:
  --session <pid>  Language-server process identifier; inferred when exactly one session is live.
  --json           Write the versioned machine-readable response envelope.
  -?, -h, --help   Show help and usage information
```

## csls sessions watch

```text
Description:
  Watch live csls sessions until canceled.

Usage:
  csls sessions watch [options]

Options:
  --json          Write one versioned JSON envelope per observed session change.
  -?, -h, --help  Show help and usage information
```

## csls workspace restore

```text
Description:
  Restore loaded solution and project entry points, then reload the workspace.

Usage:
  csls workspace restore [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls workspace reload

```text
Description:
  Reload the workspace while preserving unsaved document overlays.

Usage:
  csls workspace reload [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls workspace restart-build-host

```text
Description:
  Recreate Roslyn workspace hosts while preserving unsaved document overlays.

Usage:
  csls workspace restart-build-host [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls workspace clear-cache

```text
Description:
  Clear retained diagnostic, semantic-token, and pending-edit results.

Usage:
  csls workspace clear-cache [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls requests list

```text
Description:
  List queued and running requests.

Usage:
  csls requests list [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --cursor <cursor>   Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>     Maximum number of result items from 1 through 200. [default: 100]
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls requests cancel

```text
Description:
  Cancel one queued or running request.

Usage:
  csls requests cancel <correlation-id> [options]

Arguments:
  <correlation-id>  Stable correlation identifier from the live request list.

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls trace start

```text
Description:
  Start request lifecycle tracing for a session.

Usage:
  csls trace start [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls trace stop

```text
Description:
  Stop request lifecycle tracing and return its entries.

Usage:
  csls trace stop [options]

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls query hover

```text
Description:
  Resolve hover information at a document position.

Usage:
  csls query hover <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query diagnostics

```text
Description:
  Get compiler and analyzer diagnostics for one document.

Usage:
  csls query diagnostics <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --session <pid>            Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>         Select this workspace or start a transient session when none is live.
  --previous-result-id <id>  Opaque result identifier from a prior diagnostic response.
  --cursor <cursor>          Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>            Maximum number of result items from 1 through 200. [default: 100]
  --json                     Write the versioned machine-readable response envelope.
  -?, -h, --help             Show help and usage information
```

## csls query completion

```text
Description:
  Get bounded Roslyn completion candidates at one document position.

Usage:
  csls query completion <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query definition

```text
Description:
  Find source definitions for the symbol at one document position.

Usage:
  csls query definition <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query declaration

```text
Description:
  Find source declarations for the symbol at one document position.

Usage:
  csls query declaration <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query type-definition

```text
Description:
  Find source definitions for the symbol's type.

Usage:
  csls query type-definition <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query implementation

```text
Description:
  Find source implementations for the symbol at one document position.

Usage:
  csls query implementation <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query selection-range

```text
Description:
  Get the nested syntax selection at one document position.

Usage:
  csls query selection-range <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query highlights

```text
Description:
  Get semantic symbol occurrences within one document.

Usage:
  csls query highlights <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls query references

```text
Description:
  Find source references for the symbol at one document position.

Usage:
  csls query references <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --cursor <cursor>                Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>                  Maximum number of result items from 1 through 200. [default: 100]
  --json                           Write the versioned machine-readable response envelope.
  --include-declaration            Include symbol declaration locations in reference results.
  -?, -h, --help                   Show help and usage information
```

## csls query document-symbols

```text
Description:
  Get the hierarchical declarations in one document.

Usage:
  csls query document-symbols <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --cursor <cursor>   Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>     Maximum number of result items from 1 through 200. [default: 100]
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls query symbols

```text
Description:
  Search source declarations across the current workspace.

Usage:
  csls query symbols <pattern> [options]

Arguments:
  <pattern>  Declaration name or fuzzy search pattern.

Options:
  --session <pid>     Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>  Select this workspace or start a transient session when none is live.
  --cursor <cursor>   Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>     Maximum number of result items from 1 through 200. [default: 100]
  --json              Write the versioned machine-readable response envelope.
  -?, -h, --help      Show help and usage information
```

## csls query signature-help

```text
Description:
  Get overload-aware signature help at one document position.

Usage:
  csls query signature-help <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls edit rename

```text
Description:
  Preview a semantic cross-document rename.

Usage:
  csls edit rename <document> <new-name> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.
  <new-name>  Valid replacement C# identifier.

Options:
  --line <number> (REQUIRED)       Zero-based UTF-16 line number.
  --character <number> (REQUIRED)  Zero-based UTF-16 character offset.
  --session <pid>                  Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>               Select this workspace or start a transient session when none is live.
  --apply                          Explicitly apply the one-use plan after all preconditions pass.
  --json                           Write the versioned machine-readable response envelope.
  -?, -h, --help                   Show help and usage information
```

## csls edit format

```text
Description:
  Preview complete-document Roslyn formatting.

Usage:
  csls edit format <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --tab-size <number>  Indentation width from 1 through 32. [default: 4]
  --tabs               Use tabs instead of spaces for indentation.
  --session <pid>      Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>   Select this workspace or start a transient session when none is live.
  --apply              Explicitly apply the one-use plan after all preconditions pass.
  --json               Write the versioned machine-readable response envelope.
  -?, -h, --help       Show help and usage information
```

## csls edit code-action

```text
Description:
  Preview concrete Roslyn code actions.

Usage:
  csls edit code-action <document> [options]

Arguments:
  <document>  Absolute or current-directory-relative C# document path.

Options:
  --kind <category>     Hierarchical code-action category. [default: source.organizeImports]
  --line <number>       Zero-based line containing the code-action target.
  --character <number>  Zero-based UTF-16 character containing the code-action target.
  --session <pid>       Language-server process identifier; inferred when exactly one session is live.
  --workspace <path>    Select this workspace or start a transient session when none is live.
  --cursor <cursor>     Opaque continuation cursor returned by the previous JSON result page.
  --limit <count>       Maximum number of result items from 1 through 200. [default: 100]
  --apply               Explicitly apply the single returned action after all preconditions pass.
  --json                Write the versioned machine-readable response envelope.
  -?, -h, --help        Show help and usage information
```

## csls agent mcp

```text
Description:
  Launch the separately installed csls MCP server.

Usage:
  csls agent mcp [options]

Options:
  --session <session>      Attach to the csls language-server process with this identifier.
  --socket <socket>        Attach to this absolute csls Unix-domain-socket path.
  --workspace <workspace>  Start a transient csls session for this workspace path.
  -?, -h, --help           Show help and usage information
```

## csls agent init

```text
Description:
  Create a reusable csls agent skill file.

Usage:
  csls agent init [options]

Options:
  --path <path>   Write the skill file to this path instead of ./SKILL.md.
  --force         Replace an existing skill file.
  --stdout        Write the skill content to standard output instead of a file.
  --json          Write the versioned machine-readable response envelope.
  -?, -h, --help  Show help and usage information
```
