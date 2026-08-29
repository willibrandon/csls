using Csls.Core;
using Csls.Server;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Csls.Web.Worker;

/// <summary>
/// Owns one browser RPC session and every production language-server resource.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserLanguageServerSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _sessionSource = new();
    private readonly BrowserLspClientConnection _client;
    private readonly BrowserLspDispatcher _dispatcher;
    private readonly RequestScheduler _scheduler;
    private readonly WorkspaceManager _workspaceManager;
    private readonly LanguageServer _languageServer;
    private int _disposeState;

    /// <summary>
    /// Creates and starts one production language-server session over complete messages.
    /// </summary>
    /// <param name="sendMessageAsync">Sends one complete JSON-RPC message to JavaScript.</param>
    internal BrowserLanguageServerSession(
        Func<string, CancellationToken, ValueTask> sendMessageAsync)
    {
        BrowserLanguageServerHost.ReportStatus("creatingScheduler");
        _scheduler = new RequestScheduler();
        BrowserLanguageServerHost.ReportStatus("creatingWorkspaceLoader");
        string[] referencePaths =
        [
            .. Directory
                .EnumerateFiles("/references", "*.dll", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
        ];
        var workspaceLoader = new SynchronizedWorkspaceLoader(referencePaths);
        BrowserLanguageServerHost.ReportStatus("creatingWorkspaceLogger");
        NullLogger<WorkspaceManager> workspaceLogger = NullLogger<WorkspaceManager>.Instance;
        BrowserLanguageServerHost.ReportStatus("creatingWorkspaceManager");
        _workspaceManager = new WorkspaceManager(
            workspaceLogger,
            workspaceLoader);
        BrowserLanguageServerHost.ReportStatus("creatingClientConnection");
        _client = new BrowserLspClientConnection(sendMessageAsync);
        BrowserLanguageServerHost.ReportStatus("creatingLanguageServer");
        var logFilter = new LanguageServerLogFilter();
        _languageServer = new LanguageServer(
            _scheduler,
            _workspaceManager,
            _client,
            NullLogger<LanguageServer>.Instance,
            logFilter);
        BrowserLanguageServerHost.ReportStatus("creatingDispatcher");
        _dispatcher = new BrowserLspDispatcher(
            _languageServer,
            _client,
            sendMessageAsync);
        BrowserLanguageServerHost.ReportStatus("sessionReady");
    }

    /// <summary>
    /// Accepts one complete serialized message from the browser language client.
    /// </summary>
    /// <param name="method">The request or notification method, when present.</param>
    /// <param name="requestId">The serialized request identifier, when present.</param>
    /// <param name="parameterObject">The JavaScript parameter object, when present.</param>
    /// <param name="parameters">The serialized parameters, when present.</param>
    /// <param name="result">The serialized response result, when present.</param>
    /// <param name="error">The serialized response error, when present.</param>
    /// <returns>A task that completes after the bounded channel accepts the message.</returns>
    internal ValueTask ReceiveAsync(
        string? method,
        string? requestId,
        JSObject? parameterObject,
        string? parameters,
        string? result,
        string? error) =>
        _dispatcher.ReceiveAsync(
            method,
            requestId,
            parameterObject,
            parameters,
            result,
            error,
            _sessionSource.Token);

    /// <summary>
    /// Stops RPC dispatch and releases the scheduler, Roslyn workspace, and transport.
    /// </summary>
    /// <returns>A task that completes after the session is fully released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _sessionSource.CancelAsync().ConfigureAwait(false);
        _dispatcher.Dispose();
        _client.Dispose();
        await _languageServer.DisposeAsync().ConfigureAwait(false);
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _workspaceManager.DisposeAsync().ConfigureAwait(false);
        _sessionSource.Dispose();
    }
}
