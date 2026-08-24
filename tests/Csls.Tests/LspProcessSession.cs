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
    private readonly ExternalWorkloadLease _workloadLease;
    private int _initializationCompleted;

    private LspProcessSession(
        Process process,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc,
        ExternalWorkloadLease workloadLease)
    {
        _process = process;
        _standardErrorTask = standardErrorTask;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
        _workloadLease = workloadLease;
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
    /// <param name="client">The optional bidirectional LSP client target.</param>
    /// <param name="environmentVariables">The optional child-process environment overrides.</param>
    /// <returns>A connected process session.</returns>
    internal static LspProcessSession Start(
        string displayName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        LspTestClient? client = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
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

        if (environmentVariables is not null)
        {
            foreach ((string name, string value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        var workloadLease = ExternalWorkloadLease.Acquire();
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"The {displayName} process did not start.");
        }
        catch
        {
            workloadLease.Dispose();
            throw;
        }

        try
        {
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
            if (client is not null)
            {
                Func<ConfigurationParams, CancellationToken, Task<JsonElement?[]>> handler =
                    client.GetConfigurationAsync;
                var attribute = new JsonRpcMethodAttribute("workspace/configuration")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    handler.Method,
                    handler.Target ?? throw new InvalidOperationException(
                        "The configuration handler has no client target."),
                    attribute);

                Func<WorkspaceDiagnosticProgressParams, Task> progressHandler =
                    client.PublishWorkspaceDiagnosticProgressAsync;
                var progressAttribute = new JsonRpcMethodAttribute("$/progress")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    progressHandler.Method,
                    progressHandler.Target ?? throw new InvalidOperationException(
                        "The progress handler has no client target."),
                    progressAttribute);

                Func<PublishDiagnosticsParams, Task> diagnosticHandler =
                    client.PublishDiagnosticsAsync;
                var diagnosticAttribute = new JsonRpcMethodAttribute(
                    "textDocument/publishDiagnostics")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    diagnosticHandler.Method,
                    diagnosticHandler.Target ?? throw new InvalidOperationException(
                        "The diagnostic handler has no client target."),
                    diagnosticAttribute);
            }

            rpc.StartListening();
            return new LspProcessSession(
                process,
                standardErrorTask,
                formatter,
                messageHandler,
                rpc,
                workloadLease);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                process.Dispose();
                workloadLease.Dispose();
            }

            throw;
        }
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
        using var capabilities = JsonDocument.Parse(
            """
            {
              "textDocument": {
                "diagnostic": {}
              }
            }
            """);
        return await InitializeAsync(
            workspacePath,
            capabilities.RootElement,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with explicit client capabilities and returns its raw result.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="capabilities">The exact client capability object.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        string workspacePath,
        JsonElement capabilities,
        CancellationToken cancellationToken)
    {
        return await InitializeAsync(
            [workspacePath],
            capabilities,
            initializationOptions: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with explicit folders, capabilities, and initialization settings.
    /// </summary>
    /// <param name="workspacePaths">The ordered absolute workspace directories.</param>
    /// <param name="capabilities">The exact client capability object.</param>
    /// <param name="initializationOptions">The optional initialization configuration payload.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        IReadOnlyList<string> workspacePaths,
        JsonElement capabilities,
        JsonElement? initializationOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspacePaths);
        if (workspacePaths.Count == 0)
        {
            throw new ArgumentException("At least one workspace path is required.", nameof(workspacePaths));
        }

        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new InitializeParams
            {
                ProcessId = Environment.ProcessId,
                ClientInfo = new ClientInfo { Name = "Csls.ParityTests" },
                RootUri = DocumentUri.FromFileSystemPath(workspacePaths[0]),
                WorkspaceFolders =
                [
                    .. workspacePaths.Select(path => new WorkspaceFolder
                    {
                        Uri = DocumentUri.FromFileSystemPath(path),
                        Name = Path.GetFileName(path)
                    })
                ],
                Capabilities = capabilities,
                InitializationOptions = initializationOptions
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the initialized notification once for this real process session.
    /// </summary>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task CompleteInitializationAsync()
    {
        return Interlocked.Exchange(ref _initializationCompleted, 1) == 0
            ? _rpc.NotifyWithParameterObjectAsync(
                "initialized",
                new InitializedParams())
            : Task.CompletedTask;
    }

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
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<FoldingRange>>(
            "textDocument/foldingRange",
            new FoldingRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
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
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentLink>>(
            "textDocument/documentLink",
            new DocumentLinkParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
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
    /// Prepares a call-hierarchy item at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The prepared callable items.</returns>
    internal Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyItem>>(
            "textDocument/prepareCallHierarchy",
            new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests incoming calls for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared callable item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct incoming calls.</returns>
    internal Task<IReadOnlyList<CallHierarchyIncomingCall>> RequestIncomingCallsAsync(
        CallHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyIncomingCall>>(
            "callHierarchy/incomingCalls",
            new CallHierarchyIncomingCallsParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests outgoing calls for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared callable item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct outgoing calls.</returns>
    internal Task<IReadOnlyList<CallHierarchyOutgoingCall>> RequestOutgoingCallsAsync(
        CallHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyOutgoingCall>>(
            "callHierarchy/outgoingCalls",
            new CallHierarchyOutgoingCallsParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Prepares a type-hierarchy item at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The prepared type items.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "textDocument/prepareTypeHierarchy",
            new TypeHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests direct supertypes for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared type item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct source supertypes.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> RequestSupertypesAsync(
        TypeHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "typeHierarchy/supertypes",
            new TypeHierarchySupertypesParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests direct subtypes for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared type item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct source subtypes.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> RequestSubtypesAsync(
        TypeHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "typeHierarchy/subtypes",
            new TypeHierarchySubtypesParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests semantic inlay hints in one visible test document range.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The visible UTF-16 range.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered inlay hints.</returns>
    internal Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(
        string documentPath,
        LspRange range,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<InlayHint>>(
            "textDocument/inlayHint",
            new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Range = range
            },
            cancellationToken);

    /// <summary>
    /// Resolves deferred semantic details for one test inlay hint.
    /// </summary>
    /// <param name="hint">The server-produced hint.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The resolved inlay hint.</returns>
    internal Task<InlayHint> ResolveInlayHintAsync(
        InlayHint hint,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<InlayHint>(
            "inlayHint/resolve",
            hint,
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

    private static WorkspaceFolder CreateWorkspaceFolder(string path) =>
        new()
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Name = Path.GetFileName(path)
        };

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
    /// Requests range-limited formatting edits from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestRangeFormattingAsync(
        string documentPath,
        LspRange range,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/rangeFormatting",
            new DocumentRangeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Range = range,
                Options = options
            },
            cancellationToken);

    /// <summary>
    /// Requests localized formatting after one character is typed in the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The position around which formatting should occur.</param>
    /// <param name="character">The character that triggered formatting.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The localized non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestOnTypeFormattingAsync(
        string documentPath,
        Position position,
        string character,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/onTypeFormatting",
            new DocumentOnTypeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Character = character,
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
        CancellationToken cancellationToken) => RequestCodeActionsAsync(
            documentPath,
            range,
            only,
            [],
            cancellationToken);

    /// <summary>
    /// Requests concrete code actions with the client diagnostics for the target range.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="only">The optional requested code-action categories.</param>
    /// <param name="diagnostics">The client diagnostics intersecting the action context.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    internal Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        string documentPath,
        LspRange range,
        IReadOnlyList<string>? only,
        IReadOnlyList<Diagnostic> diagnostics,
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
                    Diagnostics = diagnostics,
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
    /// Requests configured edits immediately before one document is saved.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="reason">The reason the editor is saving the document.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping save-time edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestSaveFormattingAsync(
        string documentPath,
        TextDocumentSaveReason reason,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/willSaveWaitUntil",
            new WillSaveTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Reason = reason
            },
            cancellationToken);

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
        await RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        return await ExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends shutdown without exit so the terminating workspace state remains observable.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the server acknowledges shutdown.</returns>
    internal async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        object? shutdownResult = await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            cancellationToken).ConfigureAwait(false);
        if (shutdownResult is not null)
        {
            throw new InvalidDataException("The LSP shutdown response must be null.");
        }
    }

    /// <summary>
    /// Sends exit and verifies that the real language-server process terminates successfully.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> ExitAsync(CancellationToken cancellationToken)
    {
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
        try
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
        finally
        {
            _workloadLease.Dispose();
        }
    }
}
