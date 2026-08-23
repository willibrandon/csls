using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Defines the explicitly registered methods implemented by a csls control session.
/// </summary>
public interface IControlRpcTarget
{
    /// <summary>
    /// Gets the current language-server session state.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The current session information.</returns>
    Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current bounded dashboard snapshot from real session state.
    /// </summary>
    /// <param name="request">The optional expensive dashboard data to evaluate.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The current workspace, diagnostic, request, host, and cache state.</returns>
    Task<ControlDashboardSnapshot> GetDashboardSnapshotAsync(
        ControlDashboardRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores every current workspace entry point and reloads the resulting Roslyn state.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    Task<ControlWorkspaceOperationResult> RestoreWorkspaceAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Reloads every current workspace root while preserving unsaved document overlays.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    Task<ControlWorkspaceOperationResult> ReloadWorkspaceAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Recreates every Roslyn workspace host while preserving unsaved document overlays.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    Task<ControlWorkspaceOperationResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes every retained diagnostic, semantic-token, and pending-edit cache entry.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The completed workspace operation result.</returns>
    Task<ControlWorkspaceOperationResult> ClearCachesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves hover information from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The optional hover information.</returns>
    Task<ControlHoverResult> GetHoverAsync(
        ControlHoverRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets compiler and analyzer diagnostics from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and prior result identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>A complete or unchanged document diagnostic report.</returns>
    Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        ControlDiagnosticRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets bounded completion candidates from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The ordered completion list.</returns>
    Task<CompletionList> GetCompletionAsync(
        ControlCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source definitions from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    Task<IReadOnlyList<Location>> GetDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source declarations from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    Task<IReadOnlyList<Location>> GetDeclarationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source type definitions from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    Task<IReadOnlyList<Location>> GetTypeDefinitionAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source implementations from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    Task<IReadOnlyList<Location>> GetImplementationAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets nested syntax selections from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and ordered UTF-16 positions.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    Task<IReadOnlyList<SelectionRange>> GetSelectionRangesAsync(
        ControlSelectionRangeRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets semantic document highlights from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered document highlights.</returns>
    Task<IReadOnlyList<DocumentHighlight>> GetDocumentHighlightsAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source references from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path, position, and declaration behavior.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    Task<IReadOnlyList<Location>> GetReferencesAsync(
        ControlNavigationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the hierarchical declarations in one document snapshot.
    /// </summary>
    /// <param name="request">The absolute document path.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded declaration hierarchy.</returns>
    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        ControlDocumentRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Searches source declarations across the current workspace snapshot.
    /// </summary>
    /// <param name="request">The declaration search pattern.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered workspace symbols.</returns>
    Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        ControlWorkspaceSymbolRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one workspace symbol against the current workspace generation.
    /// </summary>
    /// <param name="symbol">The unresolved workspace symbol.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The workspace symbol with its exact range populated.</returns>
    Task<WorkspaceSymbol> ResolveWorkspaceSymbolAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets overload-aware signature help from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>Signature help, or null when no supported argument list is active.</returns>
    Task<SignatureHelp?> GetSignatureHelpAsync(
        ControlSignatureHelpRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Previews a version-aware semantic rename edit without applying it.
    /// </summary>
    /// <param name="request">The target symbol and replacement identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The one-use edit plan and exact application preconditions.</returns>
    Task<ControlEditPlan> PreviewRenameAsync(
        ControlRenameRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Previews complete-document formatting without applying it.
    /// </summary>
    /// <param name="request">The target document and formatting preferences.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The one-use formatting plan and exact application preconditions.</returns>
    Task<ControlEditPlan> PreviewFormattingAsync(
        ControlFormattingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets concrete Roslyn code actions for one source range.
    /// </summary>
    /// <param name="request">The target range and optional action categories.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        ControlCodeActionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly applies one unexpired edit plan after all preconditions pass.
    /// </summary>
    /// <param name="request">The one-use edit plan identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The new generation and changed document paths.</returns>
    Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        ControlApplyEditPlanRequest request,
        CancellationToken cancellationToken);
}
