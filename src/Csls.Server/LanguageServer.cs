using Csls.Core;
using Csls.Protocol;
using Csls.Rpc;
using Csls.Workspaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Csls.Server;

/// <summary>
/// Coordinates LSP lifecycle, request scheduling, and immutable Roslyn workspaces.
/// </summary>
public sealed partial class LanguageServer : ILspRpcTarget, IAsyncDisposable
{
    private const int MaximumFoldingRanges = 5_000;
    private const int WorkspaceDiagnosticPartialResultSize = 128;
    private readonly RequestScheduler _scheduler;
    private readonly WorkspaceManager _workspaceManager;
    private readonly LspClientConnection _client;
    private readonly ILogger<LanguageServer> _logger;
    private readonly LanguageServerLogFilter _logFilter;
    private readonly TaskCompletionSource _exitRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _exitSource = new();
    private bool _completionMarkdownSupport;
    private bool _completionSnippetSupport;
    private int _foldingRangeLimit = MaximumFoldingRanges;
    private bool _lineFoldingOnly;
    private bool _supportsCollapsedFoldingText;
    private bool _supportsCommentFoldingKind = true;
    private bool _supportsImportsFoldingKind = true;
    private bool _supportsRegionFoldingKind = true;
    private bool _supportsConfigurationPull;
    private bool _supportsCreateFileWorkspaceEdits;
    private bool _supportsPullDiagnostics;
    private bool _hoverMarkdownSupport;
    private bool _signatureMarkdownSupport;
    private LanguageServerConfiguration _configuration = new();
    private string[] _rootPaths = [];
    private int _lifecycleState;
    private int _workspacePhase;
    private int _disposeState;

    /// <summary>
    /// Initializes the language server engine and its production collaborators.
    /// </summary>
    /// <param name="scheduler">The bounded request scheduler.</param>
    /// <param name="workspaceManager">The Roslyn workspace manager.</param>
    /// <param name="client">The bidirectional LSP client connection.</param>
    /// <param name="logger">The language server logger.</param>
    /// <param name="logFilter">The dynamic logging level filter.</param>
    public LanguageServer(
        RequestScheduler scheduler,
        WorkspaceManager workspaceManager,
        LspClientConnection client,
        ILogger<LanguageServer> logger,
        LanguageServerLogFilter logFilter)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(workspaceManager);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(logFilter);
        _scheduler = scheduler;
        _workspaceManager = workspaceManager;
        _client = client;
        _logger = logger;
        _logFilter = logFilter;
    }

    /// <summary>
    /// Gets the current ordered server lifecycle state.
    /// </summary>
    public ServerLifecycleState LifecycleState =>
        (ServerLifecycleState)Volatile.Read(ref _lifecycleState);

    /// <summary>
    /// Gets the token canceled when the client sends the LSP exit notification.
    /// </summary>
    public CancellationToken ExitToken => _exitSource.Token;

    /// <summary>
    /// Gets one lock-free observation of the bounded request scheduler.
    /// </summary>
    /// <returns>The current scheduler counters and limits.</returns>
    public RequestSchedulerSnapshot GetRequestSchedulerSnapshot() => _scheduler.GetSnapshot();

    /// <summary>
    /// Attempts to cancel one live scheduled request by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <returns>True when cancellation was delivered to a live request.</returns>
    public Task<bool> TryCancelRequestAsync(Guid correlationId) =>
        _scheduler.TryCancelAsync(correlationId);

    /// <summary>
    /// Starts one bounded trace of scheduled request lifecycle activity.
    /// </summary>
    /// <returns>The newly active trace observation.</returns>
    public RequestTraceSnapshot StartRequestTrace() => _scheduler.StartTrace();

    /// <summary>
    /// Stops the active request trace and returns its final observation.
    /// </summary>
    /// <returns>The stopped trace observation.</returns>
    public RequestTraceSnapshot StopRequestTrace() => _scheduler.StopTrace();

    /// <summary>
    /// Gets the current or most recently stopped request trace observation.
    /// </summary>
    /// <returns>The current bounded trace observation.</returns>
    public RequestTraceSnapshot GetRequestTraceSnapshot() => _scheduler.GetTraceSnapshot();

    /// <summary>
    /// Gets the number of encoded semantic-token results retained by this session.
    /// </summary>
    public int SemanticTokenCacheEntries => _semanticTokensCache.Count;

    /// <summary>
    /// Inspects the current immutable workspace generation through scheduler ordering.
    /// </summary>
    /// <param name="includeDiagnostics">Whether to evaluate compiler and analyzer diagnostics.</param>
    /// <param name="cancellationToken">The inspection cancellation token.</param>
    /// <returns>The current workspace, project, document, diagnostic, host, and cache state.</returns>
    public Task<WorkspaceInspectionSnapshot> InspectWorkspaceAsync(
        bool includeDiagnostics,
        CancellationToken cancellationToken) =>
        _scheduler.ScheduleAsync(
            "workspace/inspect",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            context => new ValueTask<WorkspaceInspectionSnapshot>(
                _workspaceManager.InspectAsync(
                    includeDiagnostics,
                    context.CancellationToken)),
            cancellationToken);

    /// <inheritdoc />
    public Task<InitializeResult> InitializeAsync(
        InitializeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        Transition(ServerLifecycleState.Created, ServerLifecycleState.InitializeResponded);
        _completionMarkdownSupport = SupportsDocumentationMarkdown(
            parameters.Capabilities,
            "completion");
        _completionSnippetSupport = SupportsCompletionSnippets(parameters.Capabilities);
        _hoverMarkdownSupport = SupportsDocumentationMarkdown(
            parameters.Capabilities,
            "hover");
        _signatureMarkdownSupport = SupportsDocumentationMarkdown(
            parameters.Capabilities,
            "signatureHelp");
        _foldingRangeLimit = GetFoldingRangeLimit(parameters.Capabilities);
        _lineFoldingOnly = SupportsFoldingRangeBoolean(
            parameters.Capabilities,
            "lineFoldingOnly");
        _supportsCollapsedFoldingText = SupportsCollapsedFoldingText(
            parameters.Capabilities);
        _supportsCommentFoldingKind = SupportsFoldingRangeKind(
            parameters.Capabilities,
            FoldingRangeKind.Comment);
        _supportsImportsFoldingKind = SupportsFoldingRangeKind(
            parameters.Capabilities,
            FoldingRangeKind.Imports);
        _supportsRegionFoldingKind = SupportsFoldingRangeKind(
            parameters.Capabilities,
            FoldingRangeKind.Region);
        _supportsConfigurationPull = SupportsBooleanCapability(
            parameters.Capabilities,
            "workspace",
            "configuration");
        _supportsCreateFileWorkspaceEdits = SupportsWorkspaceResourceOperation(
            parameters.Capabilities,
            "create");
        _supportsPullDiagnostics = SupportsObjectCapability(
            parameters.Capabilities,
            "textDocument",
            "diagnostic");
        LanguageServerConfiguration configuration = ParseConfiguration(
            parameters.InitializationOptions);
        _configuration = configuration;
        _logFilter.SetMinimumLevel(configuration.LogLevel);
        _rootPaths = ResolveRootPaths(parameters);
        Volatile.Write(ref _workspacePhase, (int)ServerWorkspacePhase.Configured);
        return Task.FromResult(new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                Experimental = new ExperimentalServerCapabilities
                {
                    CSharp = new CSharpExperimentalServerCapabilities
                    {
                        MetadataUris = true
                    }
                },
                Workspace = new WorkspaceServerCapabilities
                {
                    WorkspaceFolders = new WorkspaceFoldersServerCapabilities
                    {
                        Supported = true,
                        ChangeNotifications = true
                    },
                    FileOperations = CreateFileOperationOptions()
                },
                PositionEncoding = "utf-16",
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                    Save = true,
                    WillSaveWaitUntil = true
                },
                HoverProvider = true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "csls",
                    InterFileDependencies = true,
                    WorkspaceDiagnostics = true
                },
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = true,
                    TriggerCharacters = [".", "(", "#", "\"", "<", "/"]
                },
                DefinitionProvider = true,
                DeclarationProvider = true,
                TypeDefinitionProvider = true,
                ImplementationProvider = true,
                SelectionRangeProvider = true,
                FoldingRangeProvider = true,
                LinkedEditingRangeProvider = true,
                DocumentHighlightProvider = true,
                DocumentLinkProvider = new DocumentLinkOptions
                {
                    ResolveProvider = false
                },
                MonikerProvider = true,
                SemanticTokensProvider = new SemanticTokensOptions
                {
                    Legend = CSharpSemanticTokensLegend.Create(),
                    Full = new SemanticTokensFullOptions
                    {
                        Delta = true
                    },
                    Range = false
                },
                CallHierarchyProvider = true,
                TypeHierarchyProvider = true,
                InlayHintProvider = new InlayHintOptions
                {
                    ResolveProvider = true
                },
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                WorkspaceSymbolProvider = new WorkspaceSymbolOptions
                {
                    ResolveProvider = true
                },
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = ["(", ","],
                    RetriggerCharacters = [")"]
                },
                RenameProvider = new RenameOptions
                {
                    PrepareProvider = true
                },
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions
                {
                    FirstTriggerCharacter = "}",
                    MoreTriggerCharacter = [";", "\n"]
                },
                CodeActionProvider = new CodeActionOptions
                {
                    CodeActionKinds = _supportsCreateFileWorkspaceEdits
                        ? ["quickfix", "refactor", "source.organizeImports"]
                        : ["quickfix", "source.organizeImports"],
                    ResolveProvider = false
                }
            },
            ServerInfo = new ServerInfo
            {
                Name = "csls",
                Version = typeof(LanguageServer).Assembly.GetName().Version?.ToString()
            }
        });
    }

    /// <inheritdoc />
    public async Task InitializedAsync(
        InitializedParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        Transition(ServerLifecycleState.InitializeResponded, ServerLifecycleState.Running);
        try
        {
            await _scheduler.ScheduleAsync(
                "initialized",
                RequestMode.ReadWrite,
                () => _workspaceManager.Generation,
                async context =>
                {
                    if (Interlocked.CompareExchange(
                            ref _workspacePhase,
                            (int)ServerWorkspacePhase.Loading,
                            (int)ServerWorkspacePhase.Configured) !=
                        (int)ServerWorkspacePhase.Configured)
                    {
                        return false;
                    }

                    if (_supportsConfigurationPull)
                    {
                        await PullConfigurationCoreAsync(context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await ApplyConfigurationCoreAsync(
                            _configuration,
                            context.CancellationToken).ConfigureAwait(false);
                    }

                    await _workspaceManager
                        .LoadAsync(_rootPaths, context.CancellationToken)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(
                ref _workspacePhase,
                (int)ServerWorkspacePhase.Uninitialized,
                (int)ServerWorkspacePhase.Loading);
            Interlocked.CompareExchange(
                ref _workspacePhase,
                (int)ServerWorkspacePhase.Uninitialized,
                (int)ServerWorkspacePhase.Configured);
            throw;
        }

        if (Interlocked.CompareExchange(
                ref _workspacePhase,
                (int)ServerWorkspacePhase.Ready,
                (int)ServerWorkspacePhase.Loading) ==
            (int)ServerWorkspacePhase.Loading)
        {
            LogInitialized(_rootPaths.Length);
        }
    }

    /// <inheritdoc />
    public Task<object?> ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServerLifecycleState current = LifecycleState;
        if (current is not ServerLifecycleState.Running and
            not ServerLifecycleState.InitializeResponded)
        {
            throw new InvalidOperationException($"Cannot shut down a server in state {current}.");
        }

        Volatile.Write(ref _lifecycleState, (int)ServerLifecycleState.ShuttingDown);
        Volatile.Write(ref _workspacePhase, (int)ServerWorkspacePhase.ShuttingDown);
        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public async Task ExitAsync()
    {
        Volatile.Write(ref _lifecycleState, (int)ServerLifecycleState.Exited);
        Volatile.Write(ref _workspacePhase, (int)ServerWorkspacePhase.ShuttingDown);
        _exitRequested.TrySetResult();
        await _exitSource.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidOpenAsync(
        DidOpenTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        long diagnosticRequestId = await _scheduler.ScheduleAsync(
            "textDocument/didOpen",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .OpenDocumentAsync(parameters.TextDocument, context.CancellationToken)
                    .ConfigureAwait(false);
                RegisterPushDiagnosticRequest(parameters.TextDocument.Uri, context.Ordinal);
                return context.Ordinal;
            },
            cancellationToken).ConfigureAwait(false);
        await PublishDiagnosticsAsync(
            parameters.TextDocument.Uri,
            diagnosticRequestId,
            delay: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidChangeAsync(
        DidChangeTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        long diagnosticRequestId = await _scheduler.ScheduleAsync(
            "textDocument/didChange",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .ChangeDocumentAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                RegisterPushDiagnosticRequest(parameters.TextDocument.Uri, context.Ordinal);
                return context.Ordinal;
            },
            cancellationToken).ConfigureAwait(false);
        await PublishDiagnosticsAsync(
            parameters.TextDocument.Uri,
            diagnosticRequestId,
            delay: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidCloseAsync(
        DidCloseTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        long diagnosticRequestId = await _scheduler.ScheduleAsync(
            "textDocument/didClose",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .CloseDocumentAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                RegisterPushDiagnosticRequest(parameters.TextDocument.Uri, context.Ordinal);
                return context.Ordinal;
            },
            cancellationToken).ConfigureAwait(false);
        await PublishDiagnosticsAsync(
            parameters.TextDocument.Uri,
            diagnosticRequestId,
            delay: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidSaveAsync(
        DidSaveTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        cancellationToken.ThrowIfCancellationRequested();
        if (_supportsPullDiagnostics)
        {
            return;
        }

        long diagnosticRequestId = await _scheduler.ScheduleAsync(
            "textDocument/didSave",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            context =>
            {
                RegisterPushDiagnosticRequest(parameters.TextDocument.Uri, context.Ordinal);
                return new ValueTask<long>(context.Ordinal);
            },
            cancellationToken).ConfigureAwait(false);
        await PublishDiagnosticsAsync(
            parameters.TextDocument.Uri,
            diagnosticRequestId,
            delay: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TextEdit>> WillSaveWaitUntilAsync(
        WillSaveTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Reason is not (
            TextDocumentSaveReason.Manual or
            TextDocumentSaveReason.AfterDelay or
            TextDocumentSaveReason.FocusOut))
        {
            throw new InvalidDataException(
                $"Unsupported text document save reason {(int)parameters.Reason}.");
        }

        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/willSaveWaitUntil",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                if (!_configuration.FormatOnSave)
                {
                    return [];
                }

                IReadOnlyList<TextEdit> edits = await _workspaceManager
                    .GetSaveFormattingEditsAsync(
                        parameters.TextDocument,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while save-time formatting edits were being computed.");
                }

                return edits;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(
        DocumentDiagnosticParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/diagnostic",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                DocumentDiagnosticReport report = await _workspaceManager
                    .GetDiagnosticsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while diagnostics were being computed.");
                }

                return report;
            },
            cancellationToken);
    }

    private async Task PublishWorkspaceDiagnosticPartialResultsAsync(
        WorkspaceDiagnosticReport report,
        JsonElement token,
        CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < report.Items.Count; offset +=
            WorkspaceDiagnosticPartialResultSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(
                WorkspaceDiagnosticPartialResultSize,
                report.Items.Count - offset);
            var items = new WorkspaceDocumentDiagnosticReport[count];
            for (int index = 0; index < count; index++)
            {
                items[index] = report.Items[offset + index];
            }

            await _client.PublishWorkspaceDiagnosticProgressAsync(
                new WorkspaceDiagnosticProgressParams
                {
                    Token = token,
                    Value = new WorkspaceDiagnosticReport { Items = items }
                }).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<WorkspaceDiagnosticReport> WorkspaceDiagnosticAsync(
        WorkspaceDiagnosticParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        if (parameters.PartialResultToken is JsonElement token &&
            (token.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                token.ValueKind == JsonValueKind.Number && !token.TryGetInt32(out _)))
        {
            throw new InvalidDataException(
                "A workspace diagnostic partial result token must be an integer or string.");
        }

        return _scheduler.ScheduleAsync(
            "workspace/diagnostic",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                WorkspaceDiagnosticReport report = await _workspaceManager
                    .GetWorkspaceDiagnosticsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while diagnostics were being computed.");
                }

                if (parameters.PartialResultToken is JsonElement partialResultToken)
                {
                    await PublishWorkspaceDiagnosticPartialResultsAsync(
                        report,
                        partialResultToken,
                        context.CancellationToken).ConfigureAwait(false);
                    return new WorkspaceDiagnosticReport();
                }

                return report;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompletionList> CompletionAsync(
        CompletionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/completion",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                CompletionList completion = await _workspaceManager
                    .GetCompletionsAsync(
                        parameters,
                        _completionSnippetSupport,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while completion was being computed.");
                }

                return completion;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompletionItem> CompletionResolveAsync(
        CompletionItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "completionItem/resolve",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                CompletionItem resolvedItem = await _workspaceManager
                    .ResolveCompletionAsync(
                        item,
                        context.CancellationToken,
                        _completionMarkdownSupport)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while completion was being resolved.");
                }

                return resolvedItem;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> DefinitionAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/definition",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Location> locations = await _workspaceManager
                    .GetDefinitionsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while definitions were being computed.");
                }

                return locations;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> DeclarationAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/declaration",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Location> locations = await _workspaceManager
                    .GetDeclarationsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while declarations were being computed.");
                }

                return locations;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> TypeDefinitionAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/typeDefinition",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Location> locations = await _workspaceManager
                    .GetTypeDefinitionsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while type definitions were being computed.");
                }

                return locations;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> ImplementationAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/implementation",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Location> locations = await _workspaceManager
                    .GetImplementationsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while implementations were being computed.");
                }

                return locations;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SelectionRange>> SelectionRangeAsync(
        SelectionRangeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/selectionRange",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<SelectionRange> ranges = await _workspaceManager
                    .GetSelectionRangesAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while selection ranges were being computed.");
                }

                return ranges;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FoldingRange>> FoldingRangeAsync(
        FoldingRangeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/foldingRange",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<FoldingRange> ranges = await _workspaceManager
                    .GetFoldingRangesAsync(
                        parameters,
                        _foldingRangeLimit,
                        _lineFoldingOnly,
                        _supportsCollapsedFoldingText,
                        _supportsCommentFoldingKind,
                        _supportsImportsFoldingKind,
                        _supportsRegionFoldingKind,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while folding ranges were being computed.");
                }

                return ranges;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<LinkedEditingRanges?> LinkedEditingRangeAsync(
        LinkedEditingRangeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/linkedEditingRange",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                LinkedEditingRanges? ranges = await _workspaceManager
                    .GetLinkedEditingRangesAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while linked editing ranges were being computed.");
                }

                return ranges;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentHighlight>> DocumentHighlightAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/documentHighlight",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<DocumentHighlight> highlights = await _workspaceManager
                    .GetDocumentHighlightsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while document highlights were being computed.");
                }

                return highlights;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentLink>> DocumentLinkAsync(
        DocumentLinkParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/documentLink",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<DocumentLink> links = await _workspaceManager
                    .GetDocumentLinksAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while document links were being computed.");
                }

                return links;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Moniker>> MonikerAsync(
        MonikerParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/moniker",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Moniker> monikers = await _workspaceManager
                    .GetMonikersAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while symbol monikers were being computed.");
                }

                return monikers;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CSharpMetadataResponse?> CSharpMetadataAsync(
        CSharpMetadataParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "csharp/metadata",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                CSharpMetadataResponse? response = await _workspaceManager
                    .GetCSharpMetadataAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while virtual source was being produced.");
                }

                return response;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> ReferencesAsync(
        ReferenceParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/references",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<Location> locations = await _workspaceManager
                    .GetReferencesAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while references were being computed.");
                }

                return locations;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Hover?> HoverAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/hover",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                Hover? hover = await _workspaceManager
                    .GetHoverAsync(
                        parameters,
                        context.CancellationToken,
                        _hoverMarkdownSupport)
                    .ConfigureAwait(false);
                return _workspaceManager.Generation == context.WorkspaceGeneration
                    ? hover
                    : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolAsync(
        DocumentSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/documentSymbol",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<DocumentSymbol> symbols = await _workspaceManager
                    .GetDocumentSymbolsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while document symbols were being computed.");
                }

                return symbols;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkspaceSymbol>> WorkspaceSymbolAsync(
        WorkspaceSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "workspace/symbol",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<WorkspaceSymbol> symbols = await _workspaceManager
                    .GetWorkspaceSymbolsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while workspace symbols were being computed.");
                }

                return symbols;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<WorkspaceSymbol> WorkspaceSymbolResolveAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "workspaceSymbol/resolve",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(_workspaceManager.ResolveWorkspaceSymbol(symbol));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SignatureHelp?> SignatureHelpAsync(
        SignatureHelpParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/signatureHelp",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                SignatureHelp? signatureHelp = await _workspaceManager
                    .GetSignatureHelpAsync(
                        parameters,
                        context.CancellationToken,
                        _signatureMarkdownSupport)
                    .ConfigureAwait(false);
                return _workspaceManager.Generation == context.WorkspaceGeneration
                    ? signatureHelp
                    : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/prepareRename",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                PrepareRenameResult? result = await _workspaceManager
                    .PrepareRenameAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                return _workspaceManager.Generation == context.WorkspaceGeneration
                    ? result
                    : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<WorkspaceEdit> RenameAsync(
        RenameParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/rename",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                WorkspaceEdit edit = await _workspaceManager
                    .GetRenameEditAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while rename edits were being computed.");
                }

                return edit;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TextEdit>> FormattingAsync(
        DocumentFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/formatting",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<TextEdit> edits = await _workspaceManager
                    .GetFormattingEditsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while formatting edits were being computed.");
                }

                return edits;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TextEdit>> RangeFormattingAsync(
        DocumentRangeFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/rangeFormatting",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<TextEdit> edits = await _workspaceManager
                    .GetRangeFormattingEditsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while range formatting edits were being computed.");
                }

                return edits;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TextEdit>> OnTypeFormattingAsync(
        DocumentOnTypeFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/onTypeFormatting",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<TextEdit> edits = await _workspaceManager
                    .GetOnTypeFormattingEditsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while on-type formatting edits were being computed.");
                }

                return edits;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeAction>> CodeActionAsync(
        CodeActionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "textDocument/codeAction",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<CodeAction> actions = await _workspaceManager
                    .GetCodeActionsAsync(
                        parameters,
                        _supportsCreateFileWorkspaceEdits,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while code actions were being computed.");
                }

                return actions;
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates an immutable rename edit plan for control-protocol clients.
    /// </summary>
    /// <param name="parameters">The target symbol and replacement identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The workspace edit, generation, and exact content preconditions.</returns>
    public Task<WorkspaceEditSnapshot> CreateRenameEditSnapshotAsync(
        RenameParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "csls/edit/renamePreview",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                WorkspaceEdit edit = await _workspaceManager
                    .GetRenameEditAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                WorkspaceEditSnapshot snapshot = await _workspaceManager
                    .CreateEditSnapshotAsync(edit, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while the rename plan was being created.");
                }

                return snapshot;
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates an immutable formatting edit plan for control-protocol clients.
    /// </summary>
    /// <param name="parameters">The target document and formatting preferences.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The workspace edit, generation, and exact content preconditions.</returns>
    public Task<WorkspaceEditSnapshot> CreateFormattingEditSnapshotAsync(
        DocumentFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "csls/edit/formattingPreview",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<TextEdit> edits = await _workspaceManager
                    .GetFormattingEditsAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                WorkspaceEdit edit = _workspaceManager.CreateDocumentWorkspaceEdit(
                    parameters.TextDocument.Uri,
                    edits);
                WorkspaceEditSnapshot snapshot = await _workspaceManager
                    .CreateEditSnapshotAsync(edit, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while the formatting plan was being created.");
                }

                return snapshot;
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates immutable code-action edit plans for control-protocol clients.
    /// </summary>
    /// <param name="parameters">The target range and requested action context.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The concrete actions and optional exact edit preconditions.</returns>
    public Task<IReadOnlyList<CodeActionEditSnapshot>> CreateCodeActionSnapshotsAsync(
        CodeActionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync<IReadOnlyList<CodeActionEditSnapshot>>(
            "csls/edit/codeActions",
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<CodeAction> actions = await _workspaceManager
                    .GetCodeActionsAsync(
                        parameters,
                        supportsCreateFile: true,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                var snapshots = new List<CodeActionEditSnapshot>(actions.Count);
                foreach (CodeAction action in actions)
                {
                    WorkspaceEditSnapshot? editSnapshot = action.Edit is null
                        ? null
                        : await _workspaceManager
                            .CreateEditSnapshotAsync(action.Edit, context.CancellationToken)
                            .ConfigureAwait(false);
                    snapshots.Add(new CodeActionEditSnapshot
                    {
                        Action = action,
                        EditSnapshot = editSnapshot
                    });
                }

                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while code-action plans were being created.");
                }

                return snapshots;
            },
            cancellationToken);
    }

    /// <summary>
    /// Applies one control edit plan after all generation and content checks pass.
    /// </summary>
    /// <param name="snapshot">The immutable one-use edit snapshot.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The workspace generation published after application.</returns>
    public Task<long> ApplyWorkspaceEditAsync(
        WorkspaceEditSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "csls/edit/apply",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            context => new ValueTask<long>(_workspaceManager.ApplyWorkspaceEditAsync(
                snapshot,
                context.CancellationToken)),
            cancellationToken);
    }

    /// <summary>
    /// Waits until the client sends the LSP exit notification.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes when exit is requested.</returns>
    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ValueTask wait = new(_exitRequested.Task.WaitAsync(cancellationToken));
        await wait.ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully drains requests and disposes the workspace engine.
    /// </summary>
    /// <returns>A value task that completes after resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        ClearPushDiagnosticRequests();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _semanticTokensCache.Clear();
        await _workspaceManager.DisposeAsync().ConfigureAwait(false);
        _exitSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool SupportsCompletionSnippets(JsonElement capabilities)
    {
        return capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("textDocument", out JsonElement textDocument) &&
            textDocument.ValueKind == JsonValueKind.Object &&
            textDocument.TryGetProperty("completion", out JsonElement completion) &&
            completion.ValueKind == JsonValueKind.Object &&
            completion.TryGetProperty("completionItem", out JsonElement completionItem) &&
            completionItem.ValueKind == JsonValueKind.Object &&
            completionItem.TryGetProperty("snippetSupport", out JsonElement snippetSupport) &&
            snippetSupport.ValueKind is JsonValueKind.True;
    }

    private static bool SupportsDocumentationMarkdown(
        JsonElement capabilities,
        string featureName)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("textDocument", out JsonElement textDocument) ||
            textDocument.ValueKind != JsonValueKind.Object ||
            !textDocument.TryGetProperty(featureName, out JsonElement feature) ||
            feature.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement owner = feature;
        string formatProperty = "contentFormat";
        if (string.Equals(featureName, "completion", StringComparison.Ordinal))
        {
            if (!feature.TryGetProperty("completionItem", out owner) ||
                owner.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            formatProperty = "documentationFormat";
        }
        else if (string.Equals(featureName, "signatureHelp", StringComparison.Ordinal))
        {
            if (!feature.TryGetProperty("signatureInformation", out owner) ||
                owner.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            formatProperty = "documentationFormat";
        }

        return owner.TryGetProperty(formatProperty, out JsonElement formats) &&
            formats.ValueKind == JsonValueKind.Array &&
            formats
                .EnumerateArray()
                .Any(static format =>
                    format.ValueKind == JsonValueKind.String &&
                    string.Equals(
                        format.GetString(),
                        "markdown",
                        StringComparison.Ordinal));
    }

    private static int GetFoldingRangeLimit(JsonElement capabilities)
    {
        return TryGetFoldingRangeCapability(capabilities, out JsonElement foldingRange) &&
            foldingRange.TryGetProperty("rangeLimit", out JsonElement rangeLimit) &&
            rangeLimit.TryGetInt32(out int value) &&
            value >= 0
                ? Math.Min(value, MaximumFoldingRanges)
                : MaximumFoldingRanges;
    }

    private static bool SupportsFoldingRangeBoolean(
        JsonElement capabilities,
        string propertyName)
    {
        return TryGetFoldingRangeCapability(capabilities, out JsonElement foldingRange) &&
            foldingRange.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind is JsonValueKind.True;
    }

    private static bool SupportsCollapsedFoldingText(JsonElement capabilities)
    {
        return TryGetFoldingRangeCapability(capabilities, out JsonElement foldingRange) &&
            foldingRange.TryGetProperty("foldingRange", out JsonElement foldingRangeShape) &&
            foldingRangeShape.ValueKind == JsonValueKind.Object &&
            foldingRangeShape.TryGetProperty("collapsedText", out JsonElement collapsedText) &&
            collapsedText.ValueKind is JsonValueKind.True;
    }

    private static bool SupportsFoldingRangeKind(
        JsonElement capabilities,
        string kind)
    {
        if (!TryGetFoldingRangeCapability(capabilities, out JsonElement foldingRange) ||
            !foldingRange.TryGetProperty("foldingRangeKind", out JsonElement kindCapability) ||
            kindCapability.ValueKind != JsonValueKind.Object ||
            !kindCapability.TryGetProperty("valueSet", out JsonElement valueSet) ||
            valueSet.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return valueSet
            .EnumerateArray()
            .Where(static value => value.ValueKind is JsonValueKind.String)
            .Any(value => string.Equals(value.GetString(), kind, StringComparison.Ordinal));
    }

    private static bool TryGetFoldingRangeCapability(
        JsonElement capabilities,
        out JsonElement foldingRange)
    {
        if (capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("textDocument", out JsonElement textDocument) &&
            textDocument.ValueKind == JsonValueKind.Object &&
            textDocument.TryGetProperty("foldingRange", out foldingRange) &&
            foldingRange.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        foldingRange = default;
        return false;
    }

    private static bool SupportsBooleanCapability(
        JsonElement capabilities,
        string groupName,
        string capabilityName)
    {
        return capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty(groupName, out JsonElement group) &&
            group.ValueKind == JsonValueKind.Object &&
            group.TryGetProperty(capabilityName, out JsonElement capability) &&
            capability.ValueKind is JsonValueKind.True;
    }

    private static bool SupportsObjectCapability(
        JsonElement capabilities,
        string groupName,
        string capabilityName)
    {
        return capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty(groupName, out JsonElement group) &&
            group.ValueKind == JsonValueKind.Object &&
            group.TryGetProperty(capabilityName, out JsonElement capability) &&
            capability.ValueKind == JsonValueKind.Object;
    }

    private static bool SupportsWorkspaceResourceOperation(
        JsonElement capabilities,
        string operation)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("workspace", out JsonElement workspace) ||
            workspace.ValueKind != JsonValueKind.Object ||
            !workspace.TryGetProperty("workspaceEdit", out JsonElement workspaceEdit) ||
            workspaceEdit.ValueKind != JsonValueKind.Object ||
            !workspaceEdit.TryGetProperty(
                "resourceOperations",
                out JsonElement resourceOperations) ||
            resourceOperations.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return resourceOperations
            .EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Any(value => string.Equals(
                value.GetString(),
                operation,
                StringComparison.Ordinal));
    }

    private static string[] ResolveRootPaths(InitializeParams parameters)
    {
        if (parameters.WorkspaceFolders is { Count: > 0 } folders)
        {
            return [.. folders.Select(static folder => folder.Uri.GetFileSystemPath())];
        }

        if (parameters.RootUri is DocumentUri rootUri)
        {
            return [rootUri.GetFileSystemPath()];
        }

        if (!string.IsNullOrWhiteSpace(parameters.RootPath))
        {
            return [Path.GetFullPath(parameters.RootPath)];
        }

        return [Environment.CurrentDirectory];
    }

    private void Transition(ServerLifecycleState expected, ServerLifecycleState next)
    {
        int observed = Interlocked.CompareExchange(
            ref _lifecycleState,
            (int)next,
            (int)expected);
        if (observed != (int)expected)
        {
            throw new InvalidOperationException(
                $"Expected lifecycle state {expected}, but the server is in {(ServerLifecycleState)observed}.");
        }
    }

    private void EnsureRunning()
    {
        if (LifecycleState != ServerLifecycleState.Running)
        {
            throw new InvalidOperationException("The language server is not initialized.");
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Initialized {WorkspaceFolderCount} workspace folders")]
    private partial void LogInitialized(int workspaceFolderCount);
}
