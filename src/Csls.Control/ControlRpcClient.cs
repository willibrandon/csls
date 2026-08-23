using System.Net.Sockets;
using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Control;

/// <summary>
/// Invokes versioned control services through one lazily connected Unix-domain socket.
/// </summary>
public sealed class ControlRpcClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _socketPath;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Socket? _socket;
    private NetworkStream? _stream;
    private SystemTextJsonFormatter? _formatter;
    private BoundedMessageStream? _boundedStream;
    private LengthHeaderMessageHandler? _messageHandler;
    private JsonRpc? _rpc;
    private int _disposeState;

    /// <summary>
    /// Creates a control client for an absolute Unix-domain-socket path.
    /// </summary>
    /// <param name="socketPath">The absolute live-session socket path.</param>
    public ControlRpcClient(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = Path.GetFullPath(socketPath);
    }

    /// <summary>
    /// Gets the session state from the attached language-server worker.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current session information.</returns>
    public async Task<ControlSessionInfo> GetSessionAsync(
        CancellationToken cancellationToken)
    {
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithCancellationAsync<ControlSessionInfo>(
            ControlMethods.GetSession,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlHoverResult>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<CompletionList>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<SelectionRange>>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentHighlight>>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<WorkspaceSymbol>>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<WorkspaceSymbol>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<SignatureHelp?>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlEditPlan>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlEditPlan>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<ControlCodeActionPlan>>(
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
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<ControlApplyEditPlanResult>(
            ControlMethods.ApplyEditPlan,
            request,
            cancellationToken).ConfigureAwait(false);
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

        _rpc?.Dispose();
        if (_messageHandler is not null)
        {
            await _messageHandler.DisposeAsync().ConfigureAwait(false);
        }

        if (_boundedStream is not null)
        {
            await _boundedStream.DisposeAsync().ConfigureAwait(false);
        }

        _formatter?.Dispose();
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _socket?.Dispose();
        _connectionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<JsonRpc> GetRpcAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        JsonRpc? rpc = Volatile.Read(ref _rpc);
        if (rpc is not null)
        {
            return rpc;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            rpc = Volatile.Read(ref _rpc);
            if (rpc is not null)
            {
                return rpc;
            }

            _socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await _socket.ConnectAsync(
                new UnixDomainSocketEndPoint(_socketPath),
                cancellationToken).ConfigureAwait(false);
            _stream = new NetworkStream(_socket, ownsSocket: true);
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
            _rpc = new JsonRpc(_messageHandler)
            {
                CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                DisplayName = "csls-control-client"
            };
            _rpc.StartListening();
            return _rpc;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<IReadOnlyList<Location>> GetNavigationAsync(
        string methodName,
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(request);
        JsonRpc rpc = await GetRpcAsync(cancellationToken).ConfigureAwait(false);
        return await rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>(
            methodName,
            request,
            cancellationToken).ConfigureAwait(false);
    }
}
