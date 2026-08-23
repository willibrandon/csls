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
    /// Applies pushed settings or refreshes settings through client configuration pull.
    /// </summary>
    /// <param name="parameters">The client configuration change payload.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after configuration mutation retires.</returns>
    Task DidChangeConfigurationAsync(
        DidChangeConfigurationParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds and removes workspace folders while preserving retained document overlays.
    /// </summary>
    /// <param name="parameters">The ordered workspace-folder change.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after workspace mutation retires.</returns>
    Task DidChangeWorkspaceFoldersAsync(
        DidChangeWorkspaceFoldersParams parameters,
        CancellationToken cancellationToken);

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
    /// Removes a client-owned text overlay and restores the persisted document snapshot.
    /// </summary>
    /// <param name="parameters">The closed document notification.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>A task that completes after the document mutation retires.</returns>
    Task DidCloseAsync(
        DidCloseTextDocumentParams parameters,
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
    /// Resolves lazy Roslyn documentation for one completion candidate.
    /// </summary>
    /// <param name="item">The completion candidate returned by this session.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The completion candidate enriched with documentation.</returns>
    Task<CompletionItem> CompletionResolveAsync(
        CompletionItem item,
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
    /// Finds source declarations for the symbol at one document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    Task<IReadOnlyList<Location>> DeclarationAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source definitions for the type of the symbol at one document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    Task<IReadOnlyList<Location>> TypeDefinitionAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source implementations for the symbol at one document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    Task<IReadOnlyList<Location>> ImplementationAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets nested syntax selections for ordered positions in one document.
    /// </summary>
    /// <param name="parameters">The target document and ordered positions.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    Task<IReadOnlyList<SelectionRange>> SelectionRangeAsync(
        SelectionRangeParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets semantic occurrences of one symbol within its source document.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered document highlights.</returns>
    Task<IReadOnlyList<DocumentHighlight>> DocumentHighlightAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets navigable links to existing resources named by one source document.
    /// </summary>
    /// <param name="parameters">The target text document.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered document links.</returns>
    Task<IReadOnlyList<DocumentLink>> DocumentLinkAsync(
        DocumentLinkParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets complete semantic tokens for one source document.
    /// </summary>
    /// <param name="parameters">The target text document.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The complete relative-encoded semantic-token sequence.</returns>
    Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets semantic-token edits relative to a prior complete or delta result.
    /// </summary>
    /// <param name="parameters">The target document and prior result identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>Delta edits or a complete fallback token sequence.</returns>
    Task<SemanticTokensDeltaResult> SemanticTokensFullDeltaAsync(
        SemanticTokensDeltaParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares a callable declaration for call-hierarchy expansion.
    /// </summary>
    /// <param name="parameters">The target source position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The prepared source item, or an empty list when unavailable.</returns>
    Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        CallHierarchyPrepareParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source declarations that call a prepared hierarchy item.
    /// </summary>
    /// <param name="parameters">The prepared callable item.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded direct incoming calls.</returns>
    Task<IReadOnlyList<CallHierarchyIncomingCall>> CallHierarchyIncomingCallsAsync(
        CallHierarchyIncomingCallsParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds source declarations called by a prepared hierarchy item.
    /// </summary>
    /// <param name="parameters">The prepared callable item.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded direct outgoing calls.</returns>
    Task<IReadOnlyList<CallHierarchyOutgoingCall>> CallHierarchyOutgoingCallsAsync(
        CallHierarchyOutgoingCallsParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares a source type declaration for type-hierarchy expansion.
    /// </summary>
    /// <param name="parameters">The target source position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The prepared source item, or an empty list when unavailable.</returns>
    Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        TypeHierarchyPrepareParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds direct source supertypes for a prepared type item.
    /// </summary>
    /// <param name="parameters">The prepared type item.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded direct source supertypes.</returns>
    Task<IReadOnlyList<TypeHierarchyItem>> TypeHierarchySupertypesAsync(
        TypeHierarchySupertypesParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds direct source subtypes for a prepared type item.
    /// </summary>
    /// <param name="parameters">The prepared type item.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded direct source subtypes.</returns>
    Task<IReadOnlyList<TypeHierarchyItem>> TypeHierarchySubtypesAsync(
        TypeHierarchySubtypesParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets semantic annotations for one visible document range.
    /// </summary>
    /// <param name="parameters">The target document and visible range.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered inlay hints.</returns>
    Task<IReadOnlyList<InlayHint>> InlayHintAsync(
        InlayHintParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves deferred semantic details for an inlay hint.
    /// </summary>
    /// <param name="hint">A hint previously returned by this server.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The hint populated with tooltip and acceptance edits.</returns>
    Task<InlayHint> InlayHintResolveAsync(
        InlayHint hint,
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

    /// <summary>
    /// Gets the hierarchical declarations within one source document.
    /// </summary>
    /// <param name="parameters">The target document.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded source declaration hierarchy.</returns>
    Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolAsync(
        DocumentSymbolParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Searches source declarations across the current workspace.
    /// </summary>
    /// <param name="parameters">The declaration search pattern.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded ordered workspace symbols.</returns>
    Task<IReadOnlyList<WorkspaceSymbol>> WorkspaceSymbolAsync(
        WorkspaceSymbolParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the exact source range of one workspace symbol.
    /// </summary>
    /// <param name="symbol">The workspace symbol returned by this server.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The resolved workspace symbol.</returns>
    Task<WorkspaceSymbol> WorkspaceSymbolResolveAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets overload-aware signature help at one document position.
    /// </summary>
    /// <param name="parameters">The target position and client trigger context.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>Signature help, or null when no supported argument list is active.</returns>
    Task<SignatureHelp?> SignatureHelpAsync(
        SignatureHelpParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates that the symbol at one document position can be renamed.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The editable identifier range, or null when rename is unavailable.</returns>
    Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a version-aware workspace edit that renames one source symbol.
    /// </summary>
    /// <param name="parameters">The target symbol and replacement identifier.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The complete cross-document rename edit.</returns>
    Task<WorkspaceEdit> RenameAsync(
        RenameParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Formats one complete source document using editor indentation preferences.
    /// </summary>
    /// <param name="parameters">The target document and formatting preferences.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The bounded non-overlapping document edits.</returns>
    Task<IReadOnlyList<TextEdit>> FormattingAsync(
        DocumentFormattingParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets concrete Roslyn source transformations for one document range.
    /// </summary>
    /// <param name="parameters">The target range and requested action context.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    Task<IReadOnlyList<CodeAction>> CodeActionAsync(
        CodeActionParams parameters,
        CancellationToken cancellationToken);
}
