using Csls.Protocol;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Drives real language-server requests over the shared protocol session.
/// </summary>
internal sealed partial class LspProcessSession
{
    /// <summary>
    /// Requests complete semantic tokens for one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The complete relative-encoded semantic-token result.</returns>
    internal Task<SemanticTokens> RequestSemanticTokensAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokens>(
            "textDocument/semanticTokens/full",
            new SemanticTokensParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            },
            cancellationToken);

    /// <summary>
    /// Requests semantic-token edits relative to one prior test result.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="previousResultId">The prior opaque semantic-token result identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>Delta edits or a complete fallback token result.</returns>
    internal Task<SemanticTokensDeltaResult> RequestSemanticTokensDeltaAsync(
        string documentPath,
        string previousResultId,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokensDeltaResult>(
            "textDocument/semanticTokens/full/delta",
            new SemanticTokensDeltaParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                PreviousResultId = previousResultId
            },
            cancellationToken);

    /// <summary>
    /// Prepares a call-hierarchy item at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The prepared callable items.</returns>
    internal Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyItem>>(
            "textDocument/prepareCallHierarchy",
            new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests incoming calls for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared callable item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct incoming calls.</returns>
    internal Task<IReadOnlyList<CallHierarchyIncomingCall>> RequestIncomingCallsAsync(
        CallHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyIncomingCall>>(
            "callHierarchy/incomingCalls",
            new CallHierarchyIncomingCallsParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests outgoing calls for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared callable item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct outgoing calls.</returns>
    internal Task<IReadOnlyList<CallHierarchyOutgoingCall>> RequestOutgoingCallsAsync(
        CallHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CallHierarchyOutgoingCall>>(
            "callHierarchy/outgoingCalls",
            new CallHierarchyOutgoingCallsParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Prepares a type-hierarchy item at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The prepared type items.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "textDocument/prepareTypeHierarchy",
            new TypeHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests direct supertypes for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared type item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct source supertypes.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> RequestSupertypesAsync(
        TypeHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "typeHierarchy/supertypes",
            new TypeHierarchySupertypesParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests direct subtypes for one prepared test item.
    /// </summary>
    /// <param name="item">The prepared type item.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The direct source subtypes.</returns>
    internal Task<IReadOnlyList<TypeHierarchyItem>> RequestSubtypesAsync(
        TypeHierarchyItem item,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TypeHierarchyItem>>(
            "typeHierarchy/subtypes",
            new TypeHierarchySubtypesParams
            {
                Item = item
            },
            cancellationToken);

    /// <summary>
    /// Requests semantic inlay hints in one visible test document range.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The visible UTF-16 range.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered inlay hints.</returns>
    internal Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(
        string documentPath,
        LspRange range,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<InlayHint>>(
            "textDocument/inlayHint",
            new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Range = range
            },
            cancellationToken);

    /// <summary>
    /// Resolves deferred semantic details for one test inlay hint.
    /// </summary>
    /// <param name="hint">The server-produced hint.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The resolved inlay hint.</returns>
    internal Task<InlayHint> ResolveInlayHintAsync(
        InlayHint hint,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<InlayHint>(
            "inlayHint/resolve",
            hint,
            cancellationToken);

    /// <summary>
    /// Requests unresolved reference-count annotations for one test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered unresolved declaration annotations.</returns>
    internal Task<IReadOnlyList<CodeLens>> RequestCodeLensesAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CodeLens>>(
            "textDocument/codeLens",
            new CodeLensParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            },
            cancellationToken);

    /// <summary>
    /// Resolves one server-produced reference-count annotation.
    /// </summary>
    /// <param name="codeLens">The unresolved annotation returned by the server.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The annotation populated with its reference count and editor command.</returns>
    internal Task<CodeLens> ResolveCodeLensAsync(
        CodeLens codeLens,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CodeLens>(
            "codeLens/resolve",
            codeLens,
            cancellationToken);

    /// <summary>
    /// Requests source references for the symbol at one test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="includeDeclaration">Whether the declaration location is included.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    internal Task<IReadOnlyList<Location>> RequestReferencesAsync(
        string documentPath,
        Position position,
        bool includeDeclaration,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>(
            "textDocument/references",
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Context = new ReferenceContext
                {
                    IncludeDeclaration = includeDeclaration
                }
            },
            cancellationToken);

    private Task<IReadOnlyList<Location>> RequestNavigationAsync(
        string method,
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>(
            method,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    private static WorkspaceFolder CreateWorkspaceFolder(string path) =>
        new()
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Name = Path.GetFileName(path)
        };

    /// <summary>
    /// Requests the hierarchical source declarations for one opened test document.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded declaration hierarchy.</returns>
    internal Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        RequestDocumentSymbolsAsync(
            DocumentUri.FromFileSystemPath(documentPath),
            cancellationToken);

    /// <summary>
    /// Requests the hierarchical declarations for one document URI.
    /// </summary>
    /// <param name="documentUri">The absolute target document URI.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded declaration hierarchy.</returns>
    internal Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync(
        DocumentUri documentUri,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>(
            "textDocument/documentSymbol",
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = documentUri
                }
            },
            cancellationToken);

    /// <summary>
    /// Searches declarations across the real test workspace.
    /// </summary>
    /// <param name="query">The declaration search pattern.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded ordered workspace symbols.</returns>
    internal Task<IReadOnlyList<WorkspaceSymbol>> RequestWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<WorkspaceSymbol>>(
            "workspace/symbol",
            new WorkspaceSymbolParams { Query = query },
            cancellationToken);

    /// <summary>
    /// Resolves the exact source range for one workspace symbol.
    /// </summary>
    /// <param name="symbol">The unresolved workspace symbol.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The resolved workspace symbol.</returns>
    internal Task<WorkspaceSymbol> ResolveWorkspaceSymbolAsync(
        WorkspaceSymbol symbol,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceSymbol>(
            "workspaceSymbol/resolve",
            symbol,
            cancellationToken);

    /// <summary>
    /// Requests overload-aware signature help at one opened test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>Signature help, or null when no argument list is active.</returns>
    internal Task<SignatureHelp?> RequestSignatureHelpAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<SignatureHelp?>(
            "textDocument/signatureHelp",
            new SignatureHelpParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Context = new SignatureHelpContext
                {
                    TriggerKind = SignatureHelpTriggerKind.Invoked
                }
            },
            cancellationToken);
}
