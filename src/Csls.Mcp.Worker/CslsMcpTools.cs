using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using LspRange = Csls.Protocol.Range;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded language-intelligence operations for explicitly selected csls sessions.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpTools
{
    private const int MaximumPathLength = 4096;
    private readonly McpSessionBroker _sessionBroker;

    /// <summary>
    /// Creates MCP tools backed by the shared MCP session broker.
    /// </summary>
    /// <param name="sessionBroker">The shared selector-aware session broker.</param>
    public CslsMcpTools(McpSessionBroker sessionBroker)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
    }

    /// <summary>
    /// Lists responsive csls sessions available to target from this MCP connection.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The ordered responsive session list.</returns>
    [McpServerTool(
        Name = "list_sessions",
        Title = "List csls sessions",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("List responsive csls sessions and their process, socket, lifecycle, and workspace identities.")]
    public Task<IReadOnlyList<ControlSessionInfo>> ListSessionsAsync(
        CancellationToken cancellationToken) =>
        _sessionBroker.ListSessionsAsync(cancellationToken);

    /// <summary>
    /// Gets lifecycle and workspace information for the selected csls session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The current selected session information.</returns>
    [McpServerTool(
        Name = "get_session",
        Title = "Get csls session",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get lifecycle, workspace generation, roots, and process details for the selected csls session.")]
    public Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket).GetSessionAsync(cancellationToken);

    /// <summary>
    /// Gets Roslyn hover information for an exact document position in the selected session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The optional hover result and exact source range.</returns>
    [McpServerTool(
        Name = "get_hover",
        Title = "Get C# hover",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Resolve C# signature and documentation hover information at a zero-based UTF-16 document position.")]
    public Task<ControlHoverResult> GetHoverAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return controlClient.GetHoverAsync(
            new ControlHoverRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character)
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets compiler and analyzer diagnostics for one document in the selected session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="previousResultId">The optional prior opaque diagnostic result identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>A complete or unchanged document diagnostic report.</returns>
    [McpServerTool(
        Name = "get_diagnostics",
        Title = "Get C# diagnostics",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get current compiler and analyzer diagnostics for one document in the selected csls session.")]
    public Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        CancellationToken cancellationToken,
        [Description("Optional resultId from a prior diagnostic response.")]
        string? previousResultId = null,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (previousResultId is { Length: > 256 })
        {
            throw new McpException("previousResultId cannot exceed 256 characters.");
        }

        return controlClient.GetDiagnosticsAsync(
            new ControlDiagnosticRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                PreviousResultId = previousResultId
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets bounded Roslyn completion candidates at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The ordered completion list and exact commit edits.</returns>
    [McpServerTool(
        Name = "get_completion",
        Title = "Get C# completion",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get bounded C# completion candidates and exact commit edits at a zero-based UTF-16 position.")]
    public Task<CompletionList> GetCompletionAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return controlClient.GetCompletionAsync(
            new ControlCompletionRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character)
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets source definitions for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source definition locations.</returns>
    [McpServerTool(
        Name = "get_definition",
        Title = "Get C# definition",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source definitions for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetDefinitionAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return controlClient.GetDefinitionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets source declarations for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source declaration locations.</returns>
    [McpServerTool(
        Name = "get_declaration",
        Title = "Get C# declaration",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source declarations for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetDeclarationAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return controlClient.GetDeclarationAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets source definitions for the type of the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    [McpServerTool(
        Name = "get_type_definition",
        Title = "Get C# type definition",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source definitions for the type of the C# symbol at a zero-based UTF-16 position.")]
    public Task<IReadOnlyList<Location>> GetTypeDefinitionAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return controlClient.GetTypeDefinitionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets source implementations for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source implementation locations.</returns>
    [McpServerTool(
        Name = "get_implementation",
        Title = "Get C# implementation",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source implementations for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetImplementationAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return controlClient.GetImplementationAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets the nested syntax selection at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The inner-to-outer syntax selection hierarchy.</returns>
    [McpServerTool(
        Name = "get_selection_range",
        Title = "Get C# selection range",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get the nested C# syntax selection at a zero-based UTF-16 document position.")]
    public async Task<SelectionRange> GetSelectionRangeAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest navigation = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        IReadOnlyList<SelectionRange> ranges = await controlClient.GetSelectionRangesAsync(
            new ControlSelectionRangeRequest
            {
                DocumentPath = navigation.DocumentPath,
                Positions = [navigation.Position]
            },
            cancellationToken).ConfigureAwait(false);
        return ranges.Single();
    }

    /// <summary>
    /// Gets semantic symbol occurrences within one source document.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded ordered document highlights.</returns>
    [McpServerTool(
        Name = "get_document_highlights",
        Title = "Get C# document highlights",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get semantic read, write, and declaration occurrences within one C# document.")]
    public Task<IReadOnlyList<DocumentHighlight>> GetDocumentHighlightsAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return controlClient.GetDocumentHighlightsAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets source references for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="includeDeclaration">Whether declaration locations are included.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source reference locations.</returns>
    [McpServerTool(
        Name = "get_references",
        Title = "Get C# references",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source references for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetReferencesAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        [Description("Include source declarations in the result.")]
        bool includeDeclaration,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration);
        return controlClient.GetReferencesAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets the hierarchical declarations in one document from the selected session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded source declaration hierarchy.</returns>
    [McpServerTool(
        Name = "get_document_symbols",
        Title = "Get C# document symbols",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get the hierarchical C# declarations in one document from the selected csls session.")]
    public Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPath(documentPath);
        return controlClient.GetDocumentSymbolsAsync(
            new ControlDocumentRequest
            {
                DocumentPath = Path.GetFullPath(documentPath)
            },
            cancellationToken);
    }

    /// <summary>
    /// Searches source declarations across the explicitly selected workspace.
    /// </summary>
    /// <param name="query">The declaration name or fuzzy pattern.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded resolved workspace symbols.</returns>
    [McpServerTool(
        Name = "search_workspace_symbols",
        Title = "Search C# workspace symbols",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Search bounded C# source declarations across the selected csls workspace.")]
    public Task<IReadOnlyList<WorkspaceSymbol>> SearchWorkspaceSymbolsAsync(
        [Description("Declaration name or fuzzy pattern containing at most 256 characters.")]
        string query,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (query is null || query.Length > 256)
        {
            throw new McpException("query cannot exceed 256 characters.");
        }

        return controlClient.GetWorkspaceSymbolsAsync(
            new ControlWorkspaceSymbolRequest { Query = query },
            cancellationToken);
    }

    /// <summary>
    /// Gets overload-aware signature help for one position in the selected session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>Signature help, or null when no supported argument list is active.</returns>
    [McpServerTool(
        Name = "get_signature_help",
        Title = "Get C# signature help",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get overload-aware C# signature help at a zero-based UTF-16 document position.")]
    public Task<SignatureHelp?> GetSignatureHelpAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPosition(documentPath, line, character);
        return controlClient.GetSignatureHelpAsync(
            new ControlSignatureHelpRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character)
            },
            cancellationToken);
    }

    /// <summary>
    /// Previews a semantic cross-document rename without applying any file changes.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="newName">The requested replacement identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The complete version-aware workspace edit preview.</returns>
    [McpServerTool(
        Name = "preview_rename",
        Title = "Preview C# rename",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Preview a semantic C# rename as version-aware workspace edits without applying it.")]
    public Task<ControlEditPlan> PreviewRenameAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        [Description("Valid replacement C# identifier.")]
        string newName,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPosition(documentPath, line, character);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 512)
        {
            throw new McpException("newName must contain between 1 and 512 characters.");
        }

        return controlClient.PreviewRenameAsync(
            new ControlRenameRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character),
                NewName = newName
            },
            cancellationToken);
    }

    /// <summary>
    /// Previews Roslyn formatting edits without applying any file changes.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="tabSize">The visual indentation width.</param>
    /// <param name="insertSpaces">Whether indentation uses spaces.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded non-overlapping formatting edit preview.</returns>
    [McpServerTool(
        Name = "preview_formatting",
        Title = "Preview C# formatting",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Preview complete-document Roslyn formatting edits without applying them.")]
    public Task<ControlEditPlan> PreviewFormattingAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Indentation width from 1 through 32.")]
        int tabSize,
        [Description("Use spaces instead of tabs for indentation.")]
        bool insertSpaces,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPath(documentPath);
        if (tabSize is < 1 or > 32)
        {
            throw new McpException("tabSize must be between 1 and 32.");
        }

        return controlClient.PreviewFormattingAsync(
            new ControlFormattingRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Options = new FormattingOptions
                {
                    TabSize = tabSize,
                    InsertSpaces = insertSpaces,
                    TrimTrailingWhitespace = true,
                    InsertFinalNewline = true,
                    TrimFinalNewlines = true
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Explicitly applies one unexpired edit plan after every precondition passes.
    /// </summary>
    /// <param name="planId">The unguessable one-use plan identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The new generation and changed document paths.</returns>
    [McpServerTool(
        Name = "apply_edit_plan",
        Title = "Apply csls edit plan",
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Apply one previewed csls edit plan after generation, version, and SHA-256 checks pass.")]
    public Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        [Description("One-use plan identifier returned by a preview tool.")]
        string planId,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (!Guid.TryParse(planId, out Guid parsedPlanId))
        {
            throw new McpException("planId must be a valid GUID.");
        }

        return controlClient.ApplyEditPlanAsync(
            new ControlApplyEditPlanRequest { PlanId = parsedPlanId },
            cancellationToken);
    }

    /// <summary>
    /// Gets concrete Roslyn code actions for one document range.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="startLine">The zero-based start line.</param>
    /// <param name="startCharacter">The zero-based UTF-16 start character.</param>
    /// <param name="endLine">The zero-based end line.</param>
    /// <param name="endCharacter">The zero-based UTF-16 end character.</param>
    /// <param name="kind">The optional hierarchical code-action category.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The supported code actions and optional one-use edit plans.</returns>
    [McpServerTool(
        Name = "get_code_actions",
        Title = "Get C# code actions",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get concrete Roslyn code actions for a zero-based UTF-16 source range.")]
    public Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based start line.")]
        int startLine,
        [Description("Zero-based UTF-16 start character.")]
        int startCharacter,
        [Description("Zero-based end line.")]
        int endLine,
        [Description("Zero-based UTF-16 end character.")]
        int endCharacter,
        [Description("Optional action category such as source or source.organizeImports.")]
        string? kind,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPosition(documentPath, startLine, startCharacter);
        ValidateDocumentPosition(documentPath, endLine, endCharacter);
        if (endLine < startLine ||
            (endLine == startLine && endCharacter < startCharacter))
        {
            throw new McpException("The code-action range end must not precede its start.");
        }

        if (kind is { Length: > 256 })
        {
            throw new McpException("kind cannot exceed 256 characters.");
        }

        return controlClient.GetCodeActionsAsync(
            new ControlCodeActionRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Range = new LspRange(
                    new Position(startLine, startCharacter),
                    new Position(endLine, endCharacter)),
                Only = string.IsNullOrWhiteSpace(kind) ? null : [kind]
            },
            cancellationToken);
    }

    private static ControlNavigationRequest CreateNavigationRequest(
        string documentPath,
        int line,
        int character,
        bool includeDeclaration)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return new ControlNavigationRequest
        {
            DocumentPath = Path.GetFullPath(documentPath),
            Position = new Position(line, character),
            IncludeDeclaration = includeDeclaration
        };
    }

    private static void ValidateDocumentPath(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }
    }

    private static void ValidateDocumentPosition(
        string documentPath,
        int line,
        int character)
    {
        ValidateDocumentPath(documentPath);
        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }
    }
}
