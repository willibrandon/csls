using Csls.Protocol;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Drives real language-server requests over the shared protocol session.
/// </summary>
internal sealed partial class LspProcessSession
{
    /// <summary>
    /// Validates the rename target at one opened test document position.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The rename range and placeholder, or null when rename is unavailable.</returns>
    internal Task<PrepareRenameResult?> PrepareRenameAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<PrepareRenameResult?>(
            "textDocument/prepareRename",
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Requests a version-aware workspace rename edit from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The target UTF-16 document position.</param>
    /// <param name="newName">The requested replacement identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The complete cross-document workspace edit.</returns>
    internal Task<WorkspaceEdit> RequestRenameAsync(
        string documentPath,
        Position position,
        string newName,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceEdit>(
            "textDocument/rename",
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                NewName = newName
            },
            cancellationToken);

    /// <summary>
    /// Requests complete-document formatting edits from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestFormattingAsync(
        string documentPath,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/formatting",
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Options = options
            },
            cancellationToken);

    /// <summary>
    /// Requests range-limited formatting edits from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestRangeFormattingAsync(
        string documentPath,
        LspRange range,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/rangeFormatting",
            new DocumentRangeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Range = range,
                Options = options
            },
            cancellationToken);

    /// <summary>
    /// Requests localized formatting after one character is typed in the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="position">The position around which formatting should occur.</param>
    /// <param name="character">The character that triggered formatting.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The localized non-overlapping formatting edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestOnTypeFormattingAsync(
        string documentPath,
        Position position,
        string character,
        FormattingOptions options,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/onTypeFormatting",
            new DocumentOnTypeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position,
                Character = character,
                Options = options
            },
            cancellationToken);

    /// <summary>
    /// Requests concrete code actions from the real worker.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="only">The optional requested code-action categories.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    internal Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        string documentPath,
        LspRange range,
        IReadOnlyList<string>? only,
        CancellationToken cancellationToken) => RequestCodeActionsAsync(
            documentPath,
            range,
            only,
            [],
            cancellationToken);

    /// <summary>
    /// Requests concrete code actions with the client diagnostics for the target range.
    /// </summary>
    /// <param name="documentPath">The absolute target document path.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="only">The optional requested code-action categories.</param>
    /// <param name="diagnostics">The client diagnostics intersecting the action context.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    internal Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        string documentPath,
        LspRange range,
        IReadOnlyList<string>? only,
        IReadOnlyList<Diagnostic> diagnostics,
        CancellationToken cancellationToken) =>
        RequestCodeActionsAsync(
            DocumentUri.FromFileSystemPath(documentPath),
            range,
            only,
            diagnostics,
            cancellationToken);

    /// <summary>
    /// Requests concrete code actions for one document URI.
    /// </summary>
    /// <param name="documentUri">The absolute target document URI.</param>
    /// <param name="range">The target UTF-16 source range.</param>
    /// <param name="only">The optional requested code-action categories.</param>
    /// <param name="diagnostics">The client diagnostics intersecting the action context.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The supported code actions with concrete edits.</returns>
    internal Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        DocumentUri documentUri,
        LspRange range,
        IReadOnlyList<string>? only,
        IReadOnlyList<Diagnostic> diagnostics,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CodeAction>>(
            "textDocument/codeAction",
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = documentUri
                },
                Range = range,
                Context = new CodeActionContext
                {
                    Diagnostics = diagnostics,
                    Only = only
                }
            },
            cancellationToken);

    /// <summary>
    /// Sends a document save notification through the real LSP transport.
    /// </summary>
    /// <param name="documentPath">The absolute saved document path.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task SaveDocumentAsync(string documentPath) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didSave",
            new DidSaveTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            });

    /// <summary>
    /// Requests configured edits immediately before one document is saved.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="reason">The reason the editor is saving the document.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The bounded non-overlapping save-time edits.</returns>
    internal Task<IReadOnlyList<TextEdit>> RequestSaveFormattingAsync(
        string documentPath,
        TextDocumentSaveReason reason,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/willSaveWaitUntil",
            new WillSaveTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Reason = reason
            },
            cancellationToken);

    /// <summary>
    /// Closes one test document and removes its client-owned overlay.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <returns>A task that completes after the notification is sent.</returns>
    internal Task CloseDocumentAsync(string documentPath) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didClose",
            new DidCloseTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                }
            });
}
