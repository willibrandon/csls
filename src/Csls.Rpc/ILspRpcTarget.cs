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
    /// Resolves hover information at a text document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>Hover information, or null when no symbol is present.</returns>
    Task<Hover?> HoverAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);
}
