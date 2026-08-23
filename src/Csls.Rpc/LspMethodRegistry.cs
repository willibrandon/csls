using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Rpc;

/// <summary>
/// Registers the LSP method table explicitly without assembly scanning.
/// </summary>
public static class LspMethodRegistry
{
    /// <summary>
    /// Registers every implemented LSP request and notification delegate.
    /// </summary>
    /// <param name="rpc">The StreamJsonRpc connection to configure.</param>
    /// <param name="target">The language server target implementation.</param>
    public static void Register(JsonRpc rpc, ILspRpcTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);

        AddParameterObjectMethod(
            rpc,
            "initialize",
            new Func<InitializeParams, CancellationToken, Task<InitializeResult>>(
                target.InitializeAsync));
        AddParameterObjectMethod(
            rpc,
            "initialized",
            new Func<InitializedParams, CancellationToken, Task>(target.InitializedAsync));
        rpc.AddLocalRpcMethod(
            "shutdown",
            new Func<CancellationToken, Task<object?>>(target.ShutdownAsync));
        rpc.AddLocalRpcMethod("exit", new Func<Task>(target.ExitAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/didOpen",
            new Func<DidOpenTextDocumentParams, CancellationToken, Task>(target.DidOpenAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/didChange",
            new Func<DidChangeTextDocumentParams, CancellationToken, Task>(target.DidChangeAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/didClose",
            new Func<DidCloseTextDocumentParams, CancellationToken, Task>(target.DidCloseAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/didSave",
            new Func<DidSaveTextDocumentParams, CancellationToken, Task>(target.DidSaveAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/diagnostic",
            new Func<DocumentDiagnosticParams, CancellationToken, Task<DocumentDiagnosticReport>>(
                target.DocumentDiagnosticAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/completion",
            new Func<CompletionParams, CancellationToken, Task<CompletionList>>(
                target.CompletionAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/definition",
            new Func<TextDocumentPositionParams, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.DefinitionAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/declaration",
            new Func<TextDocumentPositionParams, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.DeclarationAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/typeDefinition",
            new Func<TextDocumentPositionParams, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.TypeDefinitionAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/implementation",
            new Func<TextDocumentPositionParams, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.ImplementationAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/selectionRange",
            new Func<SelectionRangeParams, CancellationToken, Task<IReadOnlyList<SelectionRange>>>(
                target.SelectionRangeAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/documentHighlight",
            new Func<TextDocumentPositionParams, CancellationToken, Task<IReadOnlyList<DocumentHighlight>>>(
                target.DocumentHighlightAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/semanticTokens/full",
            new Func<SemanticTokensParams, CancellationToken, Task<SemanticTokens>>(
                target.SemanticTokensFullAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/semanticTokens/full/delta",
            new Func<SemanticTokensDeltaParams, CancellationToken, Task<SemanticTokensDeltaResult>>(
                target.SemanticTokensFullDeltaAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/references",
            new Func<ReferenceParams, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.ReferencesAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/hover",
            new Func<TextDocumentPositionParams, CancellationToken, Task<Hover?>>(target.HoverAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/documentSymbol",
            new Func<DocumentSymbolParams, CancellationToken, Task<IReadOnlyList<DocumentSymbol>>>(
                target.DocumentSymbolAsync));
        AddParameterObjectMethod(
            rpc,
            "workspace/symbol",
            new Func<WorkspaceSymbolParams, CancellationToken, Task<IReadOnlyList<WorkspaceSymbol>>>(
                target.WorkspaceSymbolAsync));
        AddParameterObjectMethod(
            rpc,
            "workspaceSymbol/resolve",
            new Func<WorkspaceSymbol, CancellationToken, Task<WorkspaceSymbol>>(
                target.WorkspaceSymbolResolveAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/signatureHelp",
            new Func<SignatureHelpParams, CancellationToken, Task<SignatureHelp?>>(
                target.SignatureHelpAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/prepareRename",
            new Func<TextDocumentPositionParams, CancellationToken, Task<PrepareRenameResult?>>(
                target.PrepareRenameAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/rename",
            new Func<RenameParams, CancellationToken, Task<WorkspaceEdit>>(target.RenameAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/formatting",
            new Func<DocumentFormattingParams, CancellationToken, Task<IReadOnlyList<TextEdit>>>(
                target.FormattingAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/codeAction",
            new Func<CodeActionParams, CancellationToken, Task<IReadOnlyList<CodeAction>>>(
                target.CodeActionAsync));
    }

    private static void AddParameterObjectMethod(
        JsonRpc rpc,
        string methodName,
        Delegate handler)
    {
        var attribute = new JsonRpcMethodAttribute(methodName)
        {
            UseSingleObjectParameterDeserialization = true
        };
        rpc.AddLocalRpcMethod(
            handler.Method,
            handler.Target ?? throw new InvalidOperationException(
                $"LSP method {methodName} requires an instance target."),
            attribute);
    }
}
