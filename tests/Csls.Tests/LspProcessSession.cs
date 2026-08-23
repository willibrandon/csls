using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Drives a real language-server process over its production standard streams.
/// </summary>
internal sealed class LspProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardErrorTask;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;
    private int _initializationCompleted;

    private LspProcessSession(
        Process process,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _process = process;
        _standardErrorTask = standardErrorTask;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
    }

    /// <summary>
    /// Gets the operating-system process identifier of the real language-server process.
    /// </summary>
    internal int ProcessId => _process.Id;

    /// <summary>
    /// Starts a real server process and connects a StreamJsonRpc LSP client to it.
    /// </summary>
    /// <param name="displayName">The diagnostic name for the JSON-RPC connection.</param>
    /// <param name="fileName">The server executable path.</param>
    /// <param name="arguments">The server command-line arguments.</param>
    /// <param name="workingDirectory">The isolated server working directory.</param>
    /// <returns>A connected process session.</returns>
    internal static LspProcessSession Start(
        string displayName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {displayName} process did not start.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = LspRpcJson.CreateSerializerOptions()
        };
        var messageHandler = new HeaderDelimitedMessageHandler(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            formatter);
        var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = displayName
        };
        rpc.StartListening();
        return new LspProcessSession(
            process,
            standardErrorTask,
            formatter,
            messageHandler,
            rpc);
    }

    /// <summary>
    /// Initializes the server against a real workspace and returns its raw result.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        using var capabilities = JsonDocument.Parse("{}");
        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new InitializeParams
            {
                ProcessId = Environment.ProcessId,
                ClientInfo = new ClientInfo { Name = "Csls.ParityTests" },
                RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                Capabilities = capabilities.RootElement
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes initialization and opens a real document in the server workspace.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="documentText">The exact on-disk document text.</param>
    /// <returns>A task that completes after both notifications are written.</returns>
    internal async Task OpenDocumentAsync(string documentPath, string documentText)
    {
        if (Interlocked.Exchange(ref _initializationCompleted, 1) == 0)
        {
            await _rpc.NotifyWithParameterObjectAsync(
                "initialized",
                new InitializedParams()).ConfigureAwait(false);
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = documentText
                }
            }).ConfigureAwait(false);
    }

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
    /// Requests complete semantic tokens for one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The complete relative-encoded semantic-token result.</returns>
    internal Task<SemanticTokens> RequestSemanticTokensAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokens>(
            "textDocument/semanticTokens/full",
            new SemanticTokensParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            },
            cancellationToken);

    /// <summary>
    /// Requests semantic-token edits relative to one prior test result.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="previousResultId">The prior opaque semantic-token result identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>Delta edits or a complete fallback token result.</returns>
    internal Task<SemanticTokensDeltaResult> RequestSemanticTokensDeltaAsync(
        string documentPath,
        string previousResultId,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokensDeltaResult>(
            "textDocument/semanticTokens/full/delta",
            new SemanticTokensDeltaParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                PreviousResultId = previousResultId
            },
            cancellationToken);

    /// <summary>
    /// Requests source references for the symbol at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="includeDeclaration">Whether the declaration location is included.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestReferencesAsync(
        string documentPath,
        Position position,
        bool includeDeclaration,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>(
            "textDocument/references",
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Context = new ReferenceContext
                {
                    IncludeDeclaration = includeDeclaration
                }
            },
            cancellationToken);

    private Task<IReadOnlyList<Location>> RequestNavigationAsync(
        string method,
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>(
            method,
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
    /// Requests the hierarchical source declarations for one opened test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded declaration hierarchy.</returns>
    internal Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>(
            "textDocument/documentSymbol",
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            },
            cancellationToken);

    /// <summary>
    /// Searches declarations across the real test workspace.
    /// </summary>
    /// <param name="query">The declaration search pattern.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered workspace symbols.</returns>
    internal Task<IReadOnlyList<WorkspaceSymbol>> RequestWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<WorkspaceSymbol>>(
            "workspace/symbol",
            new WorkspaceSymbolParams { Query = query },
            cancellationToken);

    /// <summary>
    /// Resolves the exact source range for one workspace symbol.
    /// </summary>
    /// <param name="symbol">The unresolved workspace symbol.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The resolved workspace symbol.</returns>
    internal Task<WorkspaceSymbol> ResolveWorkspaceSymbolAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceSymbol>(
            "workspaceSymbol/resolve",
            symbol,
            cancellationToken);

    /// <summary>
    /// Requests overload-aware signature help at one opened test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>Signature help, or null when no argument list is active.</returns>
    internal Task<SignatureHelp?> RequestSignatureHelpAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SignatureHelp?>(
            "textDocument/signatureHelp",
            new SignatureHelpParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Context = new SignatureHelpContext
                {
                    TriggerKind = SignatureHelpTriggerKind.Invoked
                }
            },
            cancellationToken);

    /// <summary>
    /// Validates the rename target at one opened test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The rename range and placeholder, or null when rename is unavailable.</returns>
    internal Task<PrepareRenameResult?> PrepareRenameAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<PrepareRenameResult?>(
            "textDocument/prepareRename",
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
    /// Requests a version-aware workspace rename edit from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="newName">The requested replacement identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The complete cross-document workspace edit.</returns>
    internal Task<WorkspaceEdit> RequestRenameAsync(
        string documentPath,
        Position position,
        string newName,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceEdit>(
            "textDocument/rename",
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                NewName = newName
            },
            cancellationToken);

    /// <summary>
    /// Requests complete-document formatting edits from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestFormattingAsync(
        string documentPath,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/formatting",
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Options = options
            },
            cancellationToken);

    /// <summary>
    /// Requests concrete code actions from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="only">The optional requested code-action categories.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    internal Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        string documentPath,
        LspRange range,
        IReadOnlyList<string>? only,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CodeAction>>(
            "textDocument/codeAction",
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Range = range,
                Context = new CodeActionContext
                {
                    Diagnostics = [],
                    Only = only
                }
            },
            cancellationToken);

    /// <summary>
    /// Sends a document save notification through the real LSP transport.
    /// </summary>
    /// <param name="documentPath">The absolute saved document path.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task SaveDocumentAsync(string documentPath) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didSave",
            new DidSaveTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            });

    /// <summary>
    /// Closes one test document and removes its client-owned overlay.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <returns>A task that completes after the notification is sent.</returns>
    internal Task CloseDocumentAsync(string documentPath) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didClose",
            new DidCloseTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            });

    /// <summary>
    /// Performs the LSP shutdown handshake and verifies a successful process exit.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> ShutdownAsync(CancellationToken cancellationToken)
    {
        object? shutdownResult = await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            cancellationToken).ConfigureAwait(false);
        if (shutdownResult is not null)
        {
            throw new InvalidDataException("The LSP shutdown response must be null.");
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "exit",
            new InitializedParams()).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        ValueTask<string> standardError = new(_standardErrorTask);
        string diagnostics = await standardError.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The language server exited with code {_process.ExitCode}: {diagnostics}");
        }

        return diagnostics;
    }

    /// <summary>
    /// Releases the RPC transport and terminates an unfinished child process tree.
    /// </summary>
    /// <returns>A task that completes after process cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }
}
