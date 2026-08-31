using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;
using System.Net.Sockets;

namespace Csls.Control;

/// <summary>
/// Invokes versioned control services through one lazily connected Unix-domain socket.
/// </summary>
public sealed class ControlRpcClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _socketPath;
    private readonly Func<CancellationToken, Task>? _workspaceReadiness;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Socket? _socket;
    private NetworkStream? _stream;
    private SystemTextJsonFormatter? _formatter;
    private BoundedMessageStream? _boundedStream;
    private LengthHeaderMessageHandler? _messageHandler;
    private JsonRpc? _rpc;
    private CancellationTokenSource? _keepAliveSource;
    private Task? _keepAliveTask;
    private int _disposeState;

    /// <summary>
    /// Creates a control client for an absolute Unix-domain-socket path.
    /// </summary>
    /// <param name="socketPath">The absolute live-session socket path.</param>
    public ControlRpcClient(string socketPath)
        : this(socketPath, workspaceReadiness: null)
    {
    }

    /// <summary>
    /// Creates a control client that gates workspace operations on asynchronous readiness.
    /// </summary>
    /// <param name="socketPath">The absolute live-session socket path.</param>
    /// <param name="workspaceReadiness">The optional workspace readiness operation.</param>
    public ControlRpcClient(
        string socketPath,
        Func<CancellationToken, Task>? workspaceReadiness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = Path.GetFullPath(socketPath);
        _workspaceReadiness = workspaceReadiness;
    }

    /// <summary>
    /// Gets the session state from the attached language-server worker.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current session information.</returns>
    public async Task<ControlSessionInfo> GetSessionAsync(
        CancellationToken cancellationToken)
    {
        return await InvokeReadAsync<ControlSessionInfo>(
            ControlMethods.GetSession,
            cancellationToken,
            waitForWorkspace: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the bounded dashboard snapshot from the attached language-server worker.
    /// </summary>
    /// <param name="request">The optional expensive dashboard data to evaluate.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current workspace, diagnostic, request, host, and cache state.</returns>
    public async Task<ControlDashboardSnapshot> GetDashboardSnapshotAsync(
        ControlDashboardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<ControlDashboardSnapshot>(
            ControlMethods.GetDashboardSnapshot,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to cancel one live request through the attached session.
    /// </summary>
    /// <param name="request">The request correlation identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The deterministic cancellation result.</returns>
    public async Task<ControlCancelRequestResult> CancelRequestAsync(
        ControlCancelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlCancelRequestResult>(
            ControlMethods.CancelRequest,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts bounded request lifecycle tracing through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The newly active trace observation.</returns>
    public async Task<ControlTraceInfo> StartTraceAsync(CancellationToken cancellationToken)
    {
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithCancellationAsync<ControlTraceInfo>(
            ControlMethods.StartTrace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops bounded request lifecycle tracing through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The stopped trace observation.</returns>
    public async Task<ControlTraceInfo> StopTraceAsync(CancellationToken cancellationToken)
    {
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithCancellationAsync<ControlTraceInfo>(
            ControlMethods.StopTrace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores every current workspace entry point through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    public Task<ControlWorkspaceOperationResult> RestoreWorkspaceAsync(
        CancellationToken cancellationToken) =>
        InvokeWorkspaceOperationAsync(ControlMethods.RestoreWorkspace, cancellationToken);

    /// <summary>
    /// Reloads every current workspace root through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    public Task<ControlWorkspaceOperationResult> ReloadWorkspaceAsync(
        CancellationToken cancellationToken) =>
        InvokeWorkspaceOperationAsync(ControlMethods.ReloadWorkspace, cancellationToken);

    /// <summary>
    /// Recreates every Roslyn workspace host through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    public Task<ControlWorkspaceOperationResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken) =>
        InvokeWorkspaceOperationAsync(ControlMethods.RestartBuildHosts, cancellationToken);

    /// <summary>
    /// Removes every retained workspace result cache entry through the attached session.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    public Task<ControlWorkspaceOperationResult> ClearCachesAsync(
        CancellationToken cancellationToken) =>
        InvokeWorkspaceOperationAsync(ControlMethods.ClearCaches, cancellationToken);

    /// <summary>
    /// Gets hover information from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The optional hover information.</returns>
    public async Task<ControlHoverResult> GetHoverAsync(
        ControlHoverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<ControlHoverResult>(
            ControlMethods.GetHover,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets compiler and analyzer diagnostics from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and prior result identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A complete or unchanged document diagnostic report.</returns>
    public async Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        ControlDiagnosticRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<DocumentDiagnosticReport>(
            ControlMethods.GetDiagnostics,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets bounded completion candidates from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The ordered completion list.</returns>
    public async Task<CompletionList> GetCompletionAsync(
        ControlCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<CompletionList>(
            ControlMethods.GetCompletion,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets source definitions from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    public Task<IReadOnlyList<Location>> GetDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        GetNavigationAsync(ControlMethods.GetDefinition, request, cancellationToken);

    /// <summary>
    /// Gets source declarations from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    public Task<IReadOnlyList<Location>> GetDeclarationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        GetNavigationAsync(ControlMethods.GetDeclaration, request, cancellationToken);

    /// <summary>
    /// Gets source type definitions from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    public Task<IReadOnlyList<Location>> GetTypeDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        GetNavigationAsync(ControlMethods.GetTypeDefinition, request, cancellationToken);

    /// <summary>
    /// Gets source implementations from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    public Task<IReadOnlyList<Location>> GetImplementationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        GetNavigationAsync(ControlMethods.GetImplementation, request, cancellationToken);

    /// <summary>
    /// Gets nested syntax selections from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and ordered UTF-16 positions.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    public async Task<IReadOnlyList<SelectionRange>> GetSelectionRangesAsync(
        ControlSelectionRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<SelectionRange>>(
            ControlMethods.GetSelectionRanges,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets semantic document highlights from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded ordered document highlights.</returns>
    public async Task<IReadOnlyList<DocumentHighlight>> GetDocumentHighlightsAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<DocumentHighlight>>(
            ControlMethods.GetDocumentHighlights,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets source references from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path, position, and declaration behavior.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    public Task<IReadOnlyList<Location>> GetReferencesAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        GetNavigationAsync(ControlMethods.GetReferences, request, cancellationToken);

    /// <summary>
    /// Gets the hierarchical declarations in one document snapshot.
    /// </summary>
    /// <param name="request">The absolute document path.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded declaration hierarchy.</returns>
    public async Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        ControlDocumentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<DocumentSymbol>>(
            ControlMethods.GetDocumentSymbols,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches source declarations across the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The declaration search pattern.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The bounded ordered workspace symbols.</returns>
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        ControlWorkspaceSymbolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<WorkspaceSymbol>>(
            ControlMethods.GetWorkspaceSymbols,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the exact source range for one workspace symbol.
    /// </summary>
    /// <param name="symbol">The unresolved workspace symbol.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The resolved workspace symbol.</returns>
    public async Task<WorkspaceSymbol> ResolveWorkspaceSymbolAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return await InvokeReadAsync<WorkspaceSymbol>(
            ControlMethods.ResolveWorkspaceSymbol,
            symbol,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets overload-aware signature help from the attached workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>Signature help, or null when no supported argument list is active.</returns>
    public async Task<SignatureHelp?> GetSignatureHelpAsync(
        ControlSignatureHelpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<SignatureHelp?>(
            ControlMethods.GetSignatureHelp,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Previews a version-aware semantic rename edit without applying it.
    /// </summary>
    /// <param name="request">The target symbol and replacement identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The one-use edit plan and exact application preconditions.</returns>
    public async Task<ControlEditPlan> PreviewRenameAsync(
        ControlRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<ControlEditPlan>(
            ControlMethods.PreviewRename,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Previews complete-document formatting without applying it.
    /// </summary>
    /// <param name="request">The target document and formatting preferences.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The one-use formatting plan and exact application preconditions.</returns>
    public async Task<ControlEditPlan> PreviewFormattingAsync(
        ControlFormattingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<ControlEditPlan>(
            ControlMethods.PreviewFormatting,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets concrete Roslyn code actions for one source range.
    /// </summary>
    /// <param name="request">The target range and optional action categories.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    public async Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        ControlCodeActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<ControlCodeActionPlan>>(
            ControlMethods.GetCodeActions,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly applies one unexpired edit plan after every precondition passes.
    /// </summary>
    /// <param name="request">The one-use edit plan identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The new generation and changed document paths.</returns>
    public async Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        ControlApplyEditPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await WaitForWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlApplyEditPlanResult>(
            ControlMethods.ApplyEditPlan,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ControlWorkspaceOperationResult> InvokeWorkspaceOperationAsync(
        string methodName,
        CancellationToken cancellationToken)
    {
        await WaitForWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithCancellationAsync<ControlWorkspaceOperationResult>(
            methodName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the control RPC connection and releases its socket resources.
    /// </summary>
    /// <returns>A task that completes after the transport is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _rpc?.Dispose();
            _rpc = null;
            await DisposeConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }

        _connectionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<JsonRpc> GetRpcAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        JsonRpc? rpc = Volatile.Read(ref _rpc);
        Task? keepAliveTask = Volatile.Read(ref _keepAliveTask);
        if (IsConnectionUsable(rpc, keepAliveTask))
        {
            return rpc!;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            rpc = Volatile.Read(ref _rpc);
            keepAliveTask = Volatile.Read(ref _keepAliveTask);
            if (IsConnectionUsable(rpc, keepAliveTask))
            {
                return rpc!;
            }

            await DisposeConnectionAsync().ConfigureAwait(false);
            try
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);
                _socket = socket;
                JsonRpc newRpc;
                ControlConnectionInfo connectionInfo;
                await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(_socketPath),
                    cancellationToken).ConfigureAwait(false);
                _stream = new NetworkStream(socket, ownsSocket: true);
                _formatter = new SystemTextJsonFormatter
                {
                    JsonSerializerOptions = ControlRpcJson.CreateSerializerOptions()
                };
                _boundedStream = new BoundedMessageStream(
                    _stream,
                    MaximumMessageBytes,
                    leaveOpen: true);
                _messageHandler = new LengthHeaderMessageHandler(
                    _boundedStream,
                    _boundedStream,
                    _formatter);
                newRpc = new JsonRpc(_messageHandler)
                {
                    CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                    DisplayName = "csls-control-client"
                };
                newRpc.CancellationStrategy = new ControlCancellationStrategy(newRpc);
                _rpc = newRpc;
                newRpc.StartListening();
                Task<ControlConnectionInfo> connectionInfoTask = newRpc
                    .InvokeWithCancellationAsync<ControlConnectionInfo>(
                        ControlMethods.GetConnectionInfo,
                        cancellationToken: CancellationToken.None);
                connectionInfo = await connectionInfoTask
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                TimeSpan keepAliveInterval = ValidateConnectionInfo(connectionInfo);
                _keepAliveSource = new CancellationTokenSource();
                _keepAliveTask = RunKeepAliveAsync(
                    newRpc,
                    keepAliveInterval,
                    _keepAliveSource.Token);
                return newRpc;
            }
            catch
            {
                await DisposeConnectionAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<TResult> InvokeReadAsync<TResult>(
        string methodName,
        CancellationToken cancellationToken,
        bool waitForWorkspace = true)
    {
        if (waitForWorkspace)
        {
            await WaitForWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        }

        bool canRetry = true;
        while (true)
        {
            JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await rpc.InvokeWithCancellationAsync<TResult>(
                    methodName,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                canRetry &&
                !cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _disposeState) == 0 &&
                IsRecoverableConnectionFailure(exception))
            {
                canRetry = false;
                await InvalidateConnectionAsync(rpc).ConfigureAwait(false);
            }
        }
    }

    private async Task<TResult> InvokeReadAsync<TResult>(
        string methodName,
        object request,
        CancellationToken cancellationToken)
    {
        await WaitForWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        bool canRetry = true;
        while (true)
        {
            JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await rpc.InvokeWithParameterObjectAsync<TResult>(
                    methodName,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                canRetry &&
                !cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _disposeState) == 0 &&
                IsRecoverableConnectionFailure(exception))
            {
                canRetry = false;
                await InvalidateConnectionAsync(rpc).ConfigureAwait(false);
            }
        }
    }

    private async Task InvalidateConnectionAsync(JsonRpc failedRpc)
    {
        await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(Volatile.Read(ref _rpc), failedRpc))
            {
                await DisposeConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private Task WaitForWorkspaceAsync(CancellationToken cancellationToken) =>
        _workspaceReadiness?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private async Task DisposeConnectionAsync()
    {
        _socket?.Dispose();
        _socket = null;
        CancellationTokenSource? keepAliveSource = _keepAliveSource;
        Task? keepAliveTask = _keepAliveTask;
        _keepAliveSource = null;
        _keepAliveTask = null;
        using (keepAliveSource)
        {
            try
            {
                if (keepAliveSource is not null)
                {
                    await keepAliveSource.CancelAsync().ConfigureAwait(false);
                }

                _rpc?.Dispose();
                _rpc = null;
                if (keepAliveTask is not null)
                {
                    await keepAliveTask.ConfigureAwait(false);
                }
            }
            finally
            {
                await DisposeTransportAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task DisposeTransportAsync()
    {
        _socket?.Dispose();
        _socket = null;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (_messageHandler is not null)
        {
            await _messageHandler.DisposeAsync().ConfigureAwait(false);
            _messageHandler = null;
        }

        if (_boundedStream is not null)
        {
            await _boundedStream.DisposeAsync().ConfigureAwait(false);
            _boundedStream = null;
        }

        _formatter?.Dispose();
        _formatter = null;
    }

    private static bool IsConnectionUsable(JsonRpc? rpc, Task? keepAliveTask) =>
        rpc is not null &&
        !rpc.Completion.IsCompleted &&
        keepAliveTask is { IsCompleted: false };

    private static bool IsRecoverableConnectionFailure(Exception exception) =>
        exception is ConnectionLostException or
            IOException or
            ObjectDisposedException or
            SocketException;

    private static TimeSpan ValidateConnectionInfo(ControlConnectionInfo connectionInfo)
    {
        ArgumentNullException.ThrowIfNull(connectionInfo);
        if (connectionInfo.ProtocolVersion != ControlProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported control protocol version {connectionInfo.ProtocolVersion}.");
        }

        if (connectionInfo.IdleTimeoutMilliseconds <= 0 ||
            connectionInfo.KeepAliveIntervalMilliseconds <= 0 ||
            connectionInfo.KeepAliveIntervalMilliseconds >=
                connectionInfo.IdleTimeoutMilliseconds)
        {
            throw new InvalidDataException(
                "The control server returned invalid connection lifetime settings.");
        }

        return TimeSpan.FromMilliseconds(connectionInfo.KeepAliveIntervalMilliseconds);
    }

    private static async Task RunKeepAliveAsync(
        JsonRpc rpc,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                bool acknowledged = await rpc.InvokeWithCancellationAsync<bool>(
                    ControlMethods.KeepAlive,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!acknowledged)
                {
                    throw new InvalidDataException(
                        "The control server rejected a connection keepalive.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (
            exception is ConnectionLostException or
                InvalidDataException or
                IOException or
                ObjectDisposedException or
                RemoteInvocationException or
                SocketException)
        {
            return;
        }
    }

    private async Task<IReadOnlyList<Location>> GetNavigationAsync(
        string methodName,
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(request);
        return await InvokeReadAsync<IReadOnlyList<Location>>(
            methodName,
            request,
            cancellationToken).ConfigureAwait(false);
    }
}
