using Csls.Core;
using Csls.Protocol;
using Csls.Rpc;
using Csls.Workspaces;
using Microsoft.Extensions.Logging;

namespace Csls.Server;

/// <summary>
/// Coordinates LSP lifecycle, request scheduling, and immutable Roslyn workspaces.
/// </summary>
public sealed partial class LanguageServer : ILspRpcTarget, IAsyncDisposable
{
    private readonly RequestScheduler _scheduler;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger<LanguageServer> _logger;
    private readonly TaskCompletionSource _exitRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _exitSource = new();
    private int _lifecycleState;
    private int _disposeState;

    /// <summary>
    /// Initializes the language server engine and its production collaborators.
    /// </summary>
    /// <param name="scheduler">The bounded request scheduler.</param>
    /// <param name="workspaceManager">The Roslyn workspace manager.</param>
    /// <param name="logger">The language server logger.</param>
    public LanguageServer(
        RequestScheduler scheduler,
        WorkspaceManager workspaceManager,
        ILogger<LanguageServer> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(workspaceManager);
        ArgumentNullException.ThrowIfNull(logger);
        _scheduler = scheduler;
        _workspaceManager = workspaceManager;
        _logger = logger;
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

    /// <inheritdoc />
    public async Task<InitializeResult> InitializeAsync(
        InitializeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Transition(ServerLifecycleState.Created, ServerLifecycleState.InitializeResponded);
        string[] rootPaths = ResolveRootPaths(parameters);
        await _scheduler.ScheduleAsync(
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .LoadAsync(rootPaths, context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        LogInitialized(rootPaths.Length);
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                PositionEncoding = "utf-16",
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                    Save = true
                },
                HoverProvider = true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "csls",
                    InterFileDependencies = true,
                    WorkspaceDiagnostics = false
                },
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = false,
                    TriggerCharacters = [".", "(", "#", "\"", "<", "/"]
                }
            },
            ServerInfo = new ServerInfo
            {
                Name = "csls",
                Version = typeof(LanguageServer).Assembly.GetName().Version?.ToString()
            }
        };
    }

    /// <inheritdoc />
    public Task InitializedAsync(
        InitializedParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        Transition(ServerLifecycleState.InitializeResponded, ServerLifecycleState.Running);
        return Task.CompletedTask;
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
        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public async Task ExitAsync()
    {
        Volatile.Write(ref _lifecycleState, (int)ServerLifecycleState.Exited);
        _exitRequested.TrySetResult();
        await _exitSource.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DidOpenAsync(
        DidOpenTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .OpenDocumentAsync(parameters.TextDocument, context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DidChangeAsync(
        DidChangeTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                await _workspaceManager
                    .ChangeDocumentAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DidSaveAsync(
        DidSaveTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(
        DocumentDiagnosticParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
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

    /// <inheritdoc />
    public Task<CompletionList> CompletionAsync(
        CompletionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                CompletionList completion = await _workspaceManager
                    .GetCompletionsAsync(parameters, context.CancellationToken)
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
    public Task<Hover?> HoverAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                Hover? hover = await _workspaceManager
                    .GetHoverAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                return _workspaceManager.Generation == context.WorkspaceGeneration
                    ? hover
                    : null;
            },
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

        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _workspaceManager.DisposeAsync().ConfigureAwait(false);
        _exitSource.Dispose();
        GC.SuppressFinalize(this);
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
