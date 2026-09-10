using Csls.Protocol;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Drives real language-server requests over the shared protocol session.
/// </summary>
internal sealed partial class LspProcessSession
{
    /// <summary>
    /// Completes initialization and opens a real document in the server workspace.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="documentText">The exact on-disk document text.</param>
    /// <param name="languageId">The client language identifier.</param>
    /// <returns>A task that completes after both notifications are written.</returns>
    internal async Task OpenDocumentAsync(
        string documentPath,
        string documentText,
        string languageId = "csharp")
    {
        await CompleteInitializationAsync().ConfigureAwait(false);

        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = languageId,
                    Version = 1,
                    Text = documentText
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that client configuration changed and should be pulled or applied.
    /// </summary>
    /// <param name="settings">The pushed configuration payload.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task ChangeConfigurationAsync(JsonElement settings) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didChangeConfiguration",
            new DidChangeConfigurationParams { Settings = settings });

    /// <summary>
    /// Sends one real workspace-folder change notification to the server.
    /// </summary>
    /// <param name="added">The absolute workspace directories to add.</param>
    /// <param name="removed">The absolute workspace directories to remove.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task ChangeWorkspaceFoldersAsync(
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didChangeWorkspaceFolders",
            new DidChangeWorkspaceFoldersParams
            {
                Event = new WorkspaceFoldersChangeEvent
                {
                    Added = [.. added.Select(CreateWorkspaceFolder)],
                    Removed = [.. removed.Select(CreateWorkspaceFolder)]
                }
            });

    /// <summary>
    /// Notifies the real server that the client created files or folders.
    /// </summary>
    /// <param name="paths">The absolute created resource paths.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task CreateFilesAsync(IReadOnlyList<string> paths) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didCreateFiles",
            new CreateFilesParams
            {
                Files =
                [
                    .. paths.Select(path => new FileCreate
                    {
                        Uri = DocumentUri.FromFileSystemPath(path)
                    })
                ]
            });

    /// <summary>
    /// Notifies the real server that the client renamed files or folders.
    /// </summary>
    /// <param name="renames">The ordered original and new absolute paths.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task RenameFilesAsync(IReadOnlyList<(string OldPath, string NewPath)> renames) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didRenameFiles",
            new RenameFilesParams
            {
                Files =
                [
                    .. renames.Select(static rename => new FileRename
                    {
                        OldUri = DocumentUri.FromFileSystemPath(rename.OldPath),
                        NewUri = DocumentUri.FromFileSystemPath(rename.NewPath)
                    })
                ]
            });

    /// <summary>
    /// Notifies the real server that the client deleted files or folders.
    /// </summary>
    /// <param name="paths">The absolute deleted resource paths.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task DeleteFilesAsync(IReadOnlyList<string> paths) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didDeleteFiles",
            new DeleteFilesParams
            {
                Files =
                [
                    .. paths.Select(path => new FileDelete
                    {
                        Uri = DocumentUri.FromFileSystemPath(path)
                    })
                ]
            });

    /// <summary>
    /// Notifies the real server about observed workspace file-system changes.
    /// </summary>
    /// <param name="changes">The ordered absolute paths and change kinds.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task ChangeWatchedFilesAsync(
        IReadOnlyList<(string Path, FileChangeType Type)> changes) =>
        _rpc.NotifyWithParameterObjectAsync(
            "workspace/didChangeWatchedFiles",
            new DidChangeWatchedFilesParams
            {
                Changes =
                [
                    .. changes.Select(static change => new FileEvent
                    {
                        Uri = DocumentUri.FromFileSystemPath(change.Path),
                        Type = change.Type
                    })
                ]
            });

    /// <summary>
    /// Requests hover information at an exact UTF-16 document position.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="position">The requested document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The raw optional hover result.</returns>
    internal Task<JsonElement?> RequestHoverAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
            "textDocument/hover",
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests Roslyn project contexts for one opened test document.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The optional Roslyn project-context response.</returns>
    internal async Task<JsonElement?> RequestRoslynProjectContextsAsync(
        string documentPath,
        CancellationToken cancellationToken)
    {
        string documentUri = JsonEncodedText
            .Encode(DocumentUri.FromFileSystemPath(documentPath).ToString())
            .ToString();
        using var parameters = JsonDocument.Parse(
            $$"""
            {
              "_vs_textDocument": {
                "uri": "{{documentUri}}"
              }
            }
            """);
        return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
            "textDocument/_vs_getProjectContexts",
            parameters.RootElement,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies ordered incremental or full-text changes to an opened test document.
    /// </summary>
    /// <param name="documentPath">The absolute changed document path.</param>
    /// <param name="version">The resulting client document version.</param>
    /// <param name="contentChanges">The ordered content changes.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task ChangeDocumentAsync(
        string documentPath,
        int version,
        IReadOnlyList<TextDocumentContentChangeEvent> contentChanges) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didChange",
            new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    Version = version
                },
                ContentChanges = contentChanges
            });

    /// <summary>
    /// Requests current pull diagnostics for one opened test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="previousResultId">The prior opaque result identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The complete or unchanged diagnostic report.</returns>
    internal Task<DocumentDiagnosticReport> RequestDiagnosticsAsync(
        string documentPath,
        string? previousResultId,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
            "textDocument/diagnostic",
            new DocumentDiagnosticParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Identifier = "csls",
                PreviousResultId = previousResultId
            },
            cancellationToken);

    /// <summary>
    /// Requests current pull diagnostics for every document in the loaded test workspace.
    /// </summary>
    /// <param name="previousResultIds">The document results already retained by the test client.</param>
    /// <param name="partialResultToken">The optional token for bounded progress notifications.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered complete or unchanged workspace diagnostic reports.</returns>
    internal Task<WorkspaceDiagnosticReport> RequestWorkspaceDiagnosticsAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        JsonElement? partialResultToken,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceDiagnosticReport>(
            "workspace/diagnostic",
            new WorkspaceDiagnosticParams
            {
                Identifier = "csls",
                PartialResultToken = partialResultToken,
                PreviousResultIds = previousResultIds
            },
            cancellationToken);

    /// <summary>
    /// Requests bounded completion candidates at one opened test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered completion list.</returns>
    internal Task<CompletionList> RequestCompletionAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CompletionList>(
            "textDocument/completion",
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Context = new CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.Invoked
                }
            },
            cancellationToken);

    /// <summary>
    /// Resolves lazy Roslyn documentation for one test completion candidate.
    /// </summary>
    /// <param name="item">The completion candidate returned by this session.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The completion candidate enriched with documentation.</returns>
    internal Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CompletionItem>(
            "completionItem/resolve",
            item,
            cancellationToken);

    /// <summary>
    /// Requests source definitions for the symbol at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestDefinitionsAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        RequestNavigationAsync(
            "textDocument/definition",
            documentPath,
            position,
            cancellationToken);

    /// <summary>
    /// Requests source declarations for the symbol at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestDeclarationsAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        RequestNavigationAsync(
            "textDocument/declaration",
            documentPath,
            position,
            cancellationToken);

    /// <summary>
    /// Requests source definitions for the type at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestTypeDefinitionsAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        RequestNavigationAsync(
            "textDocument/typeDefinition",
            documentPath,
            position,
            cancellationToken);

    /// <summary>
    /// Requests source implementations for the symbol at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestImplementationsAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        RequestNavigationAsync(
            "textDocument/implementation",
            documentPath,
            position,
            cancellationToken);

    /// <summary>
    /// Requests nested syntax selections for ordered positions in one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="positions">The ordered UTF-16 document positions.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    internal Task<IReadOnlyList<SelectionRange>> RequestSelectionRangesAsync(
        string documentPath,
        IReadOnlyList<Position> positions,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<SelectionRange>>(
            "textDocument/selectionRange",
            new SelectionRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Positions = positions
            },
            cancellationToken);

    /// <summary>
    /// Requests negotiated folding ranges for one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered folding ranges.</returns>
    internal Task<IReadOnlyList<FoldingRange>> RequestFoldingRangesAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        RequestFoldingRangesAsync(
            DocumentUri.FromFileSystemPath(documentPath),
            cancellationToken);

    /// <summary>
    /// Requests negotiated folding ranges for one document URI.
    /// </summary>
    /// <param name="documentUri">The absolute target document URI.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered folding ranges.</returns>
    internal Task<IReadOnlyList<FoldingRange>> RequestFoldingRangesAsync(
        DocumentUri documentUri,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<FoldingRange>>(
            "textDocument/foldingRange",
            new FoldingRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = documentUri
                }
            },
            cancellationToken);

    /// <summary>
    /// Requests paired linked editing ranges for one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The linked ranges, or <see langword="null"/> when none apply.</returns>
    internal Task<LinkedEditingRanges?> RequestLinkedEditingRangesAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<LinkedEditingRanges?>(
            "textDocument/linkedEditingRange",
            new LinkedEditingRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests raw linked editing JSON for protocol-shape assertions.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The raw linked editing response.</returns>
    internal Task<JsonElement> RequestLinkedEditingRangesJsonAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/linkedEditingRange",
            new LinkedEditingRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests semantic symbol highlights within one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered document highlights.</returns>
    internal Task<IReadOnlyList<DocumentHighlight>> RequestDocumentHighlightsAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentHighlight>>(
            "textDocument/documentHighlight",
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests navigable resource links for one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered document links.</returns>
    internal Task<IReadOnlyList<DocumentLink>> RequestDocumentLinksAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        RequestDocumentLinksAsync(
            DocumentUri.FromFileSystemPath(documentPath),
            cancellationToken);

    /// <summary>
    /// Requests navigable resource links for one document URI.
    /// </summary>
    /// <param name="documentUri">The absolute target document URI.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered document links.</returns>
    internal Task<IReadOnlyList<DocumentLink>> RequestDocumentLinksAsync(
        DocumentUri documentUri,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentLink>>(
            "textDocument/documentLink",
            new DocumentLinkParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = documentUri
                }
            },
            cancellationToken);

    /// <summary>
    /// Requests stable .NET monikers for one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The resolved monikers, or an empty list when none are available.</returns>
    internal Task<IReadOnlyList<Moniker>> RequestMonikersAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Moniker>>(
            "textDocument/moniker",
            new MonikerParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests the raw JSON moniker response for protocol-shape assertions.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The raw JSON moniker array returned by the server.</returns>
    internal Task<JsonElement> RequestMonikerJsonAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/moniker",
            new MonikerParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests source text for one virtual C# document from the real server process.
    /// </summary>
    /// <param name="documentUri">The generated or metadata-backed document URI.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The virtual document response, when the URI resolves.</returns>
    internal Task<CSharpMetadataResponse?> RequestCSharpMetadataAsync(
        DocumentUri documentUri,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpMetadataResponse?>(
            "csharp/metadata",
            new CSharpMetadataParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = documentUri }
            },
            cancellationToken);

    /// <summary>
    /// Requests the live scheduler-independent debug observation from the real server process.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The current workspace and request diagnostics.</returns>
    internal Task<CSharpDebugInfo> RequestDebugInfoAsync(
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpDebugInfo>(
            "$/csharp/debugInfo",
            new InitializedParams(),
            cancellationToken);

    /// <summary>
    /// Requests the current Roslyn workspace structure from the real server process.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The current workspace structure.</returns>
    internal Task<CSharpWorkspaceInfo> RequestWorkspaceInfoAsync(
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpWorkspaceInfo>(
            "$/csharp/workspaceInfo",
            new InitializedParams(),
            cancellationToken);

    /// <summary>
    /// Restores and reloads the current Roslyn workspace through the editor protocol.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The completed workspace operation.</returns>
    internal Task<CSharpWorkspaceOperationInfo> RestoreWorkspaceForClientAsync(
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpWorkspaceOperationInfo>(
            "$/csharp/workspace/restore",
            new InitializedParams(),
            cancellationToken);
}
