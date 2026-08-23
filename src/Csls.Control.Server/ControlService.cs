using Csls.Control.Contracts;
using Csls.Protocol;
using Csls.Server;
using Csls.Workspaces;

namespace Csls.Control;

/// <summary>
/// Adapts versioned control requests to the live language-server engine and workspace snapshot.
/// </summary>
public sealed class ControlService : IControlRpcTarget
{
    private readonly LanguageServer _languageServer;
    private readonly WorkspaceManager _workspaceManager;
    private readonly string _socketPath;

    /// <summary>
    /// Creates a control target for the current language-server process.
    /// </summary>
    /// <param name="languageServer">The live language-server engine.</param>
    /// <param name="workspaceManager">The live Roslyn workspace manager.</param>
    public ControlService(
        LanguageServer languageServer,
        WorkspaceManager workspaceManager)
    {
        ArgumentNullException.ThrowIfNull(languageServer);
        ArgumentNullException.ThrowIfNull(workspaceManager);
        _languageServer = languageServer;
        _workspaceManager = workspaceManager;
        _socketPath = ControlEndpoint.GetSocketPath(Environment.ProcessId);
    }

    /// <inheritdoc />
    public Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ControlSessionInfo
        {
            ProcessId = Environment.ProcessId,
            LifecycleState = _languageServer.LifecycleState.ToString(),
            WorkspaceGeneration = _workspaceManager.Generation,
            WorkspaceRoots = _workspaceManager.WorkspaceRoots,
            SocketPath = _socketPath
        });
    }

    /// <inheritdoc />
    public async Task<ControlHoverResult> GetHoverAsync(
        ControlHoverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        string documentPath = Path.GetFullPath(request.DocumentPath);
        Hover? hover = await _languageServer.HoverAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = request.Position
            },
            cancellationToken).ConfigureAwait(false);
        return new ControlHoverResult
        {
            Found = hover is not null,
            Hover = hover
        };
    }

    /// <inheritdoc />
    public Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        ControlDiagnosticRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        string documentPath = Path.GetFullPath(request.DocumentPath);
        return _languageServer.DocumentDiagnosticAsync(
            new DocumentDiagnosticParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Identifier = "csls",
                PreviousResultId = request.PreviousResultId
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompletionList> GetCompletionAsync(
        ControlCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        string documentPath = Path.GetFullPath(request.DocumentPath);
        return _languageServer.CompletionAsync(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = request.Position,
                Context = new CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.Invoked
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> GetDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams parameters = CreateNavigationParams(request);
        return _languageServer.DefinitionAsync(parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> GetReferencesAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams positionParameters = CreateNavigationParams(request);
        return _languageServer.ReferencesAsync(
            new ReferenceParams
            {
                TextDocument = positionParameters.TextDocument,
                Position = positionParameters.Position,
                Context = new ReferenceContext
                {
                    IncludeDeclaration = request.IncludeDeclaration
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        ControlDocumentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        return _languageServer.DocumentSymbolAsync(
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        ControlWorkspaceSymbolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<WorkspaceSymbol> symbols = await _languageServer.WorkspaceSymbolAsync(
            new WorkspaceSymbolParams { Query = request.Query },
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. symbols.Select(static symbol => symbol.Data is WorkspaceSymbolData data
                ? symbol with
                {
                    Location = symbol.Location with { Range = data.Range }
                }
                : symbol)
        ];
    }

    /// <inheritdoc />
    public Task<WorkspaceSymbol> ResolveWorkspaceSymbolAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken) =>
        _languageServer.WorkspaceSymbolResolveAsync(symbol, cancellationToken);

    /// <inheritdoc />
    public Task<SignatureHelp?> GetSignatureHelpAsync(
        ControlSignatureHelpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        return _languageServer.SignatureHelpAsync(
            new SignatureHelpParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                },
                Position = request.Position,
                Context = new SignatureHelpContext
                {
                    TriggerKind = SignatureHelpTriggerKind.Invoked
                }
            },
            cancellationToken);
    }

    private static TextDocumentPositionParams CreateNavigationParams(
        ControlNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        string documentPath = Path.GetFullPath(request.DocumentPath);
        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = request.Position
        };
    }
}
