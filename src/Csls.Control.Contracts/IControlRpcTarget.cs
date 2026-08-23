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
}
