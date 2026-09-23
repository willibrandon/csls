using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using static Csls.Mcp.Worker.McpDocumentValidation;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded language-intelligence operations for explicitly selected csls sessions.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpTools
{
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
        ValidateDocumentPosition(documentPath, line, character);

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
        ValidateDocumentPath(documentPath);

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
        ValidateDocumentPosition(documentPath, line, character);

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
}
