using Csls.Protocol;

namespace Csls.Rpc;

/// <summary>
/// Defines every explicitly registered LSP entry point implemented by the server engine.
/// </summary>
public interface ILspRpcTarget
{
    /// <summary>
    /// Initializes the language server and negotiates capabilities.
    /// </summary>
    /// <param name="parameters">The client initialization parameters.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The negotiated server capabilities.</returns>
    Task<InitializeResult> InitializeAsync(
        InitializeParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes client initialization and starts post-initialization work.
    /// </summary>
    /// <param name="parameters">The initialized notification parameters.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after initialization work is queued.</returns>
    Task InitializedAsync(
        InitializedParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gracefully shuts down the language server.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The required null LSP shutdown result.</returns>
    Task<object?> ShutdownAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Handles the final exit notification.
    /// </summary>
    /// <returns>A task that completes when exit state is recorded.</returns>
    Task ExitAsync();

    /// <summary>
    /// Opens a text document in the active workspace snapshot.
    /// </summary>
    /// <param name="parameters">The opened document and its complete contents.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after the document mutation retires.</returns>
    Task DidOpenAsync(
        DidOpenTextDocumentParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies ordered content changes to an opened document.
    /// </summary>
    /// <param name="parameters">The versioned document content changes.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after the document mutation retires.</returns>
    Task DidChangeAsync(
        DidChangeTextDocumentParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a client save notification for an opened document.
    /// </summary>
    /// <param name="parameters">The saved document notification.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after the notification is validated.</returns>
    Task DidSaveAsync(
        DidSaveTextDocumentParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets current compiler and analyzer diagnostics for one document.
    /// </summary>
    /// <param name="parameters">The document and prior diagnostic result identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>A complete or unchanged document diagnostic report.</returns>
    Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(
        DocumentDiagnosticParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets bounded completion candidates at one document position.
    /// </summary>
    /// <param name="parameters">The document position and trigger context.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The ordered completion list.</returns>
    Task<CompletionList> CompletionAsync(
        CompletionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source definitions for the symbol at one document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    Task<IReadOnlyList<Location>> DefinitionAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source references for the symbol at one document position.
    /// </summary>
    /// <param name="parameters">The target position and declaration inclusion behavior.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    Task<IReadOnlyList<Location>> ReferencesAsync(
        ReferenceParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves hover information at a text document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>Hover information, or null when no symbol is present.</returns>
    Task<Hover?> HoverAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);
}
