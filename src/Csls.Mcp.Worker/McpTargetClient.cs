using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;

namespace Csls.Mcp.Worker;

/// <summary>
/// Binds one tool invocation to a typed MCP target through the shared broker.
/// </summary>
internal sealed class McpTargetClient
{
    private readonly McpSessionBroker _sessionBroker;
    private readonly string? _workspace;
    private readonly int? _session;
    private readonly string? _socket;

    /// <summary>
    /// Creates a target-bound control facade.
    /// </summary>
    /// <param name="sessionBroker">The shared MCP session broker.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    internal McpTargetClient(
        McpSessionBroker sessionBroker,
        string? workspace,
        int? session,
        string? socket)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
        _workspace = workspace;
        _session = session;
        _socket = socket;
    }

    /// <summary>
    /// Gets the selected session information.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The selected session information.</returns>
    internal Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken) =>
        InvokeAsync(static (client, token) => client.GetSessionAsync(token), cancellationToken);

    /// <summary>
    /// Gets hover information from the selected session.
    /// </summary>
    /// <param name="request">The hover request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The hover response.</returns>
    internal Task<ControlHoverResult> GetHoverAsync(
        ControlHoverRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetHoverAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets diagnostics from the selected session.
    /// </summary>
    /// <param name="request">The diagnostic request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The diagnostic response.</returns>
    internal Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        ControlDiagnosticRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetDiagnosticsAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets completion candidates from the selected session.
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The completion response.</returns>
    internal Task<CompletionList> GetCompletionAsync(
        ControlCompletionRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetCompletionAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets definitions from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The definition locations.</returns>
    internal Task<IReadOnlyList<Location>> GetDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetDefinitionAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets declarations from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The declaration locations.</returns>
    internal Task<IReadOnlyList<Location>> GetDeclarationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetDeclarationAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets type definitions from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The type-definition locations.</returns>
    internal Task<IReadOnlyList<Location>> GetTypeDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetTypeDefinitionAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets implementations from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The implementation locations.</returns>
    internal Task<IReadOnlyList<Location>> GetImplementationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetImplementationAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets selection ranges from the selected session.
    /// </summary>
    /// <param name="request">The selection-range request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The selection ranges.</returns>
    internal Task<IReadOnlyList<SelectionRange>> GetSelectionRangesAsync(
        ControlSelectionRangeRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetSelectionRangesAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets document highlights from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The document highlights.</returns>
    internal Task<IReadOnlyList<DocumentHighlight>> GetDocumentHighlightsAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetDocumentHighlightsAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets references from the selected session.
    /// </summary>
    /// <param name="request">The navigation request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The reference locations.</returns>
    internal Task<IReadOnlyList<Location>> GetReferencesAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetReferencesAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets document symbols from the selected session.
    /// </summary>
    /// <param name="request">The document request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The document symbols.</returns>
    internal Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        ControlDocumentRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetDocumentSymbolsAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets workspace symbols from the selected session.
    /// </summary>
    /// <param name="request">The workspace-symbol request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The workspace symbols.</returns>
    internal Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        ControlWorkspaceSymbolRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetWorkspaceSymbolsAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets signature help from the selected session.
    /// </summary>
    /// <param name="request">The signature-help request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The optional signature help.</returns>
    internal Task<SignatureHelp?> GetSignatureHelpAsync(
        ControlSignatureHelpRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetSignatureHelpAsync(request, token), cancellationToken);

    /// <summary>
    /// Previews a rename in the selected session.
    /// </summary>
    /// <param name="request">The rename request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The edit plan.</returns>
    internal Task<ControlEditPlan> PreviewRenameAsync(
        ControlRenameRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.PreviewRenameAsync(request, token), cancellationToken);

    /// <summary>
    /// Previews formatting in the selected session.
    /// </summary>
    /// <param name="request">The formatting request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The edit plan.</returns>
    internal Task<ControlEditPlan> PreviewFormattingAsync(
        ControlFormattingRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.PreviewFormattingAsync(request, token), cancellationToken);

    /// <summary>
    /// Applies an edit plan in the selected session.
    /// </summary>
    /// <param name="request">The apply request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The apply result.</returns>
    internal Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        ControlApplyEditPlanRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ApplyEditPlanAsync(request, token), cancellationToken);

    /// <summary>
    /// Gets code actions from the selected session.
    /// </summary>
    /// <param name="request">The code-action request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The code-action plans.</returns>
    internal Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        ControlCodeActionRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.GetCodeActionsAsync(request, token), cancellationToken);

    private Task<T> InvokeAsync<T>(
        Func<ControlRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        _sessionBroker.InvokeAsync(
            _workspace,
            _session,
            _socket,
            operation,
            cancellationToken);
}
