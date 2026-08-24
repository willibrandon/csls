---
title: Language server
description: Language Server Protocol features implemented by csls.
---

`csls` advertises only capabilities backed by an active implementation. Current
language features include:

- C# compiler and analyzer diagnostics, plus project-aware Razor diagnostics, completion, hover, navigation, rename, and formatting
- completion with import edits, negotiated snippets, lazy documentation, hover, and signature help
- definitions, declarations, implementations, references, highlights, links, monikers, and linked editing
- document and workspace symbols
- semantic tokens with full and delta responses
- call hierarchy, type hierarchy, selection ranges, folding ranges, and inlay hints
- rename, move-to-file refactoring, missing-using and interface implementation quick fixes, import organization, and document, range, on-type, or opt-in save formatting
- project-aware file creation, rename, folder move, and deletion tracking

Document and workspace diagnostic pulls use the same immutable Roslyn snapshot.
Workspace results cover user C# and Razor documents across all loaded projects,
exclude build output, include versions for open files, and return unchanged reports
when the client already holds the current result. Clients that provide a partial
result token receive bounded batches through standard LSP progress notifications.
Clients without pull-diagnostic support receive complete versioned push diagnostics
after documents open, change, or save. Rapid edits are coalesced, and closing a
document clears its published diagnostics.

The server tracks open-document versions and applies incremental text changes.
Workspace loading supports solutions, projects, file-based apps, loose C# files,
multiple roots, and folder changes during a live session. File-based apps are
restored and evaluated by the .NET SDK selected for their directory. Package,
project, include, property, and SDK directives therefore use the same NuGet,
MSBuild, central package management, and `global.json` behavior as `dotnet run`.
Folder discovery loads shebang apps and directive apps with top-level statements,
while an explicit C# file workspace always uses file-based app semantics.

Unity projects are detected by their `Assets` directory and
`ProjectSettings/ProjectVersion.txt` file. csls loads the generated solution at
the project root and skips Unity's generated `Library`, `Temp`, `Logs`, and
`UserSettings` trees. A normal .NET project in a directory named `Library`
remains discoverable.

Unsaved documents survive reloads when their workspace folder remains active.
Standard workspace file-operation
notifications refresh project topology from disk. Open unsaved documents follow
file and folder renames, while deleted documents and stale diagnostics are removed.

Completion edits are computed by Roslyn. Clients that advertise snippet support
receive snippet insertion text with Roslyn's final caret position. Other clients
receive plain text. `completionItem/resolve` adds Roslyn documentation without
changing the edit, sort text, or filter text returned by the original request.
Hover, completion resolve, signature help, and parameter help preserve structured
C# XML documentation such as references, paragraphs, code, and lists. Markdown is
returned only when the client includes it in the corresponding content formats.
Inherited method and parameter documentation follows overrides and interface
implementations.
Razor views and components receive C# member and type completion from their
generated project snapshot. Commit edits map back to Razor source, including
`@using` directives required by types from unimported namespaces.

Rename works from C# and from mapped C# expressions in `.razor` and `.cshtml`
files. A single version-aware workspace edit updates Razor references, Razor-local
members, and ordinary C# declarations and references. Rename is rejected when
the new identifier would bind to a different symbol in generated Razor code.

Missing-using quick fixes use Roslyn's project and metadata indexes. Each proposed
import is applied to a temporary document, simplified, formatted, and kept only
when the unresolved name binds to the selected accessible type. The returned edit
includes the current document version when the file is open.

Interface implementation quick fixes generate required inherited methods,
properties, indexers, events, and static abstract members. Existing and default
members are left alone. csls compiles the temporary document and returns the edit
only when the selected interface is fully implemented without new compiler errors.

Move to file extracts a top-level class, struct, interface, record, enum, or
delegate from a file that contains another declaration. The result creates the
matching `.cs` file before inserting its formatted source and removing the
original declaration. The action is offered only when the client supports
ordered document changes and create-file resource operations. CLI and MCP
previews record both the source hash and the requirement that the target path
does not exist before `--apply` can write either file.

`textDocument/moniker` returns `dotnet` identifiers built from canonical assembly
identities and Roslyn documentation IDs. Strong-named assembly APIs are unique
within the scheme. Unsigned project APIs are unique within their project group,
while non-public symbols use project or document scope.

`textDocument/linkedEditingRange` links matching start and end names in XML
documentation, including nested and custom elements. Self-closing, mismatched,
and unrelated text do not produce linked ranges.

`textDocument/foldingRange` returns C# syntax, comment, import, and region folds
from the current Roslyn document snapshot. Results honor the client range limit,
line-only mode, supported kinds, and collapsed text capability.

Razor views and components use the compiler from the pinned .NET SDK. Pull
diagnostics include Razor findings and mapped C# compiler or analyzer findings
from the owning project. They follow the current unsaved `.cshtml` or `.razor`
snapshot and return to the persisted file after the editor closes it. Hover uses
the same generated project snapshot and maps Roslyn content and ranges back to
the Razor source, including symbols made available by `_Imports.razor` and
`_ViewImports.cshtml`. Definition, declaration, type definition, implementation,
and reference requests use that snapshot and map Razor locations back to their
source files.
Razor formatting indents markup and embedded C#, aligns multiline attributes,
and honors the client's tab, space, newline, and trimming settings. Range
formatting updates only the selected lines. On-type formatting updates the local
source lines after `}`, `;`, or newline. Content in `pre`, `script`, `style`, and
`textarea` elements is left unchanged. Save formatting uses project settings for
C# and stable four-space indentation for Razor and cshtml files.

[Configuration](../configuration/) is pulled through the standard workspace request
when the client supports it. Push-only clients use the same settings and precedence.

## Session control

Each language-server process creates a private Unix domain socket and a session
manifest in the user cache directory. The socket is supported by .NET on Windows,
Linux, and macOS. It is not exposed over the network.

The CLI and MCP server authenticate through operating-system file permissions and
connect to this socket. This keeps editor requests, terminal commands, and agent
requests on one Roslyn workspace.
