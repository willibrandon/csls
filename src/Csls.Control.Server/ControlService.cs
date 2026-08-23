using Csls.Control.Contracts;
using Csls.Core;
using Csls.Protocol;
using Csls.Server;
using Csls.Workspaces;
using System.Collections.Concurrent;

namespace Csls.Control;

/// <summary>
/// Adapts versioned control requests to the live language-server engine and workspace snapshot.
/// </summary>
public sealed class ControlService : IControlRpcTarget
{
    private const int MaximumPendingEditPlans = 128;
    private static readonly TimeSpan s_editPlanLifetime = TimeSpan.FromMinutes(5);
    private readonly LanguageServer _languageServer;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ControlLogBuffer _logBuffer;
    private readonly string _socketPath;
    private readonly ConcurrentDictionary<Guid, PendingControlEditPlan> _pendingEditPlans = new();

    /// <summary>
    /// Creates a control target for the current language-server process.
    /// </summary>
    /// <param name="languageServer">The live language-server engine.</param>
    /// <param name="workspaceManager">The live Roslyn workspace manager.</param>
    /// <param name="logBuffer">The bounded structured worker log buffer.</param>
    public ControlService(
        LanguageServer languageServer,
        WorkspaceManager workspaceManager,
        ControlLogBuffer logBuffer)
    {
        ArgumentNullException.ThrowIfNull(languageServer);
        ArgumentNullException.ThrowIfNull(workspaceManager);
        ArgumentNullException.ThrowIfNull(logBuffer);
        _languageServer = languageServer;
        _workspaceManager = workspaceManager;
        _logBuffer = logBuffer;
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
    public async Task<ControlDashboardSnapshot> GetDashboardSnapshotAsync(
        ControlDashboardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkspaceInspectionSnapshot workspace = await _languageServer
            .InspectWorkspaceAsync(request.IncludeDiagnostics, cancellationToken)
            .ConfigureAwait(false);
        RequestSchedulerSnapshot requests = _languageServer.GetRequestSchedulerSnapshot();
        ControlSessionInfo session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return new ControlDashboardSnapshot
        {
            Session = session,
            Workspaces =
            [
                .. workspace.Workspaces.Select(static item => new ControlWorkspaceInfo
                {
                    RootPath = item.RootPath,
                    WorkspaceKind = item.WorkspaceKind,
                    ProjectCount = item.ProjectCount,
                    DocumentCount = item.DocumentCount
                })
            ],
            Projects =
            [
                .. workspace.Projects.Select(static item => new ControlProjectInfo
                {
                    Id = item.Id,
                    Name = item.Name,
                    FilePath = item.FilePath,
                    WorkspaceRoot = item.WorkspaceRoot,
                    Language = item.Language,
                    DocumentCount = item.DocumentCount,
                    AnalyzerReferenceCount = item.AnalyzerReferenceCount
                })
            ],
            Documents =
            [
                .. workspace.Documents.Select(static item => new ControlDocumentInfo
                {
                    Id = item.Id,
                    Name = item.Name,
                    FilePath = item.FilePath,
                    ProjectName = item.ProjectName,
                    IsOpen = item.IsOpen
                })
            ],
            Diagnostics =
            [
                .. workspace.Diagnostics.Select(static item => new ControlDiagnosticInfo
                {
                    Id = item.Id,
                    Severity = item.Severity,
                    Message = item.Message,
                    ProjectName = item.ProjectName,
                    FilePath = item.FilePath,
                    Line = item.Line,
                    Character = item.Character
                })
            ],
            DiagnosticsLoaded = workspace.DiagnosticsLoaded,
            TotalDiagnostics = workspace.TotalDiagnostics,
            DiagnosticsTruncated = workspace.DiagnosticsTruncated,
            Requests = new ControlRequestSchedulerInfo
            {
                Capacity = requests.Capacity,
                ForegroundConcurrency = requests.ForegroundConcurrency,
                BackgroundConcurrency = requests.BackgroundConcurrency,
                AcceptedRequests = requests.AcceptedRequests,
                CompletedRequests = requests.CompletedRequests,
                QueuedRequests = requests.QueuedRequests,
                ActiveForegroundRequests = requests.ActiveForegroundRequests,
                ActiveBackgroundRequests = requests.ActiveBackgroundRequests,
                IsMutationActive = requests.IsMutationActive,
                IsStopping = requests.IsStopping
            },
            BuildHosts =
            [
                .. workspace.BuildHosts.Select(static item => new ControlBuildHostInfo
                {
                    ProcessId = item.ProcessId,
                    Kind = item.Kind,
                    WorkspaceRoot = item.WorkspaceRoot,
                    ProjectCount = item.ProjectCount
                })
            ],
            Caches =
            [
                new ControlCacheInfo
                {
                    Name = "project diagnostics",
                    EntryCount = workspace.DiagnosticCacheEntries
                },
                new ControlCacheInfo
                {
                    Name = "semantic tokens",
                    EntryCount = _languageServer.SemanticTokenCacheEntries,
                    Capacity = 128
                }
            ],
            Logs =
            [
                .. _logBuffer.GetSnapshot().Select(static entry => new ControlLogEntry
                {
                    Sequence = entry.Sequence,
                    Timestamp = entry.Timestamp,
                    Category = entry.Category,
                    Level = entry.Level.ToString(),
                    Message = entry.Message
                })
            ]
        };
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
    public Task<IReadOnlyList<Location>> GetDeclarationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams parameters = CreateNavigationParams(request);
        return _languageServer.DeclarationAsync(parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> GetTypeDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams parameters = CreateNavigationParams(request);
        return _languageServer.TypeDefinitionAsync(parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Location>> GetImplementationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams parameters = CreateNavigationParams(request);
        return _languageServer.ImplementationAsync(parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SelectionRange>> GetSelectionRangesAsync(
        ControlSelectionRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        return _languageServer.SelectionRangeAsync(
            new SelectionRangeParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                },
                Positions = request.Positions
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentHighlight>> GetDocumentHighlightsAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams parameters = CreateNavigationParams(request);
        return _languageServer.DocumentHighlightAsync(parameters, cancellationToken);
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

    /// <inheritdoc />
    public async Task<ControlEditPlan> PreviewRenameAsync(
        ControlRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewName);
        WorkspaceEditSnapshot snapshot = await _languageServer.CreateRenameEditSnapshotAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                },
                Position = request.Position,
                NewName = request.NewName
            },
            cancellationToken).ConfigureAwait(false);
        return CreateEditPlan("rename", snapshot);
    }

    /// <inheritdoc />
    public async Task<ControlEditPlan> PreviewFormattingAsync(
        ControlFormattingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        WorkspaceEditSnapshot snapshot = await _languageServer.CreateFormattingEditSnapshotAsync(
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                },
                Options = request.Options
            },
            cancellationToken).ConfigureAwait(false);
        return CreateEditPlan("format", snapshot);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        ControlCodeActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        IReadOnlyList<CodeActionEditSnapshot> snapshots = await _languageServer
            .CreateCodeActionSnapshotsAsync(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(
                        Path.GetFullPath(request.DocumentPath))
                },
                Range = request.Range,
                Context = new CodeActionContext
                {
                    Diagnostics = [],
                    Only = request.Only
                }
            },
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. snapshots.Select(snapshot => new ControlCodeActionPlan
            {
                Action = snapshot.Action,
                EditPlan = snapshot.EditSnapshot is null
                    ? null
                    : CreateEditPlan("code-action", snapshot.EditSnapshot)
            })
        ];
    }

    /// <inheritdoc />
    public async Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        ControlApplyEditPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_pendingEditPlans.TryRemove(request.PlanId, out PendingControlEditPlan? pendingPlan))
        {
            throw new InvalidOperationException(
                "The edit plan is unknown, expired, or has already been applied.");
        }

        if (pendingPlan.ExpiresAtUtc <= TimeProvider.System.GetUtcNow())
        {
            throw new InvalidOperationException("The edit plan has expired.");
        }

        long generation = await _languageServer.ApplyWorkspaceEditAsync(
            pendingPlan.Snapshot,
            cancellationToken).ConfigureAwait(false);
        return new ControlApplyEditPlanResult
        {
            WorkspaceGeneration = generation,
            DocumentPaths =
            [
                .. pendingPlan.Snapshot.Edit.DocumentChanges.Select(static edit =>
                    edit.TextDocument.Uri.GetFileSystemPath())
            ]
        };
    }

    private ControlEditPlan CreateEditPlan(
        string operation,
        WorkspaceEditSnapshot snapshot)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        foreach ((Guid existingPlanId, PendingControlEditPlan existingPlan) in _pendingEditPlans)
        {
            if (existingPlan.ExpiresAtUtc <= now)
            {
                _pendingEditPlans.TryRemove(existingPlanId, out _);
            }
        }

        if (_pendingEditPlans.Count >= MaximumPendingEditPlans)
        {
            throw new InvalidOperationException(
                $"The session already has {MaximumPendingEditPlans} pending edit plans.");
        }

        var planId = Guid.NewGuid();
        DateTimeOffset expiresAtUtc = now + s_editPlanLifetime;
        var pendingPlan = new PendingControlEditPlan
        {
            Snapshot = snapshot,
            ExpiresAtUtc = expiresAtUtc
        };
        if (!_pendingEditPlans.TryAdd(planId, pendingPlan))
        {
            throw new InvalidOperationException("The edit plan identifier collided.");
        }

        return new ControlEditPlan
        {
            PlanId = planId,
            Operation = operation,
            WorkspaceGeneration = snapshot.WorkspaceGeneration,
            ExpiresAtUtc = expiresAtUtc,
            Edit = snapshot.Edit,
            Preconditions =
            [
                .. snapshot.Preconditions.Select(static precondition =>
                    new ControlDocumentPrecondition
                    {
                        DocumentPath = precondition.Uri.GetFileSystemPath(),
                        Version = precondition.Version,
                        Sha256 = precondition.Sha256
                    })
            ]
        };
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
