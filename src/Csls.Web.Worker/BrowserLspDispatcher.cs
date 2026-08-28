using Csls.Protocol;
using Csls.Server;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Web.Worker;

/// <summary>
/// Dispatches the complete typed LSP method table without runtime reflection.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserLspDispatcher : IDisposable
{
    private const int InternalErrorCode = -32603;
    private const int InvalidParamsCode = -32602;
    private const int MethodNotFoundCode = -32601;
    private const int RequestCancelledCode = -32800;
    private readonly BrowserLspClientConnection _client;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incomingRequests =
        new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, ValueTask> _sendMessageAsync;
    private readonly JsonSerializerOptions _serializerOptions = LspJson.CreateSerializerOptions();
    private readonly LanguageServer _target;
    private int _disposeState;

    /// <summary>
    /// Creates a browser dispatcher for one language-server session.
    /// </summary>
    /// <param name="target">The complete typed language-server target.</param>
    /// <param name="client">The matching server-to-client connection.</param>
    /// <param name="sendMessageAsync">Sends one complete JSON-RPC message to JavaScript.</param>
    internal BrowserLspDispatcher(
        LanguageServer target,
        BrowserLspClientConnection client,
        Func<string, CancellationToken, ValueTask> sendMessageAsync)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sendMessageAsync);
        _target = target;
        _client = client;
        _sendMessageAsync = sendMessageAsync;
    }

    /// <summary>
    /// Dispatches one structured JSON-RPC message from the browser client.
    /// </summary>
    /// <param name="method">The request or notification method, when present.</param>
    /// <param name="requestId">The serialized request identifier, when present.</param>
    /// <param name="parameterObject">The JavaScript parameter object, when present.</param>
    /// <param name="parameters">The serialized parameters, when present.</param>
    /// <param name="result">The serialized response result, when present.</param>
    /// <param name="error">The serialized response error, when present.</param>
    /// <param name="cancellationToken">The session cancellation token.</param>
    /// <returns>A task that completes after the message has been handled.</returns>
    internal async ValueTask ReceiveAsync(
        string? method,
        string? requestId,
        JSObject? parameterObject,
        string? parameters,
        string? result,
        string? error,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (method is null)
        {
            _client.AcceptResponse(requestId, result, error);
            return;
        }

        if (string.Equals(method, "$/cancelRequest", StringComparison.Ordinal))
        {
            CancelRequest(parameters);
            return;
        }

        if (requestId is null)
        {
            try
            {
                await DispatchNotificationAsync(
                    method,
                    parameterObject,
                    parameters,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (IsRecoverableFailure(exception))
            {
                await SendNotificationFailureAsync(method, exception).ConfigureAwait(false);
            }

            return;
        }

        string requestKey = requestId;
        using var requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        if (!_incomingRequests.TryAdd(requestKey, requestSource))
        {
            await SendErrorAsync(
                requestId,
                InvalidParamsCode,
                "The JSON-RPC request ID is already active.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await DispatchRequestAsync(
                method,
                parameterObject,
                parameters,
                requestId,
                requestSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestSource.IsCancellationRequested)
        {
            await SendErrorAsync(
                requestId,
                RequestCancelledCode,
                "The request was cancelled.",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await SendErrorAsync(
                requestId,
                InvalidParamsCode,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        catch (LspServerCancelledException exception)
        {
            await SendErrorAsync(
                requestId,
                LspServerCancelledException.ErrorCode,
                exception.Message,
                exception.CancellationData,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await SendErrorAsync(
                requestId,
                InternalErrorCode,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            await SendErrorAsync(
                requestId,
                InternalErrorCode,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _incomingRequests.TryRemove(requestKey, out _);
        }
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OutOfMemoryException;

    /// <summary>
    /// Cancels active requests and releases dispatcher resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        foreach ((string requestId, CancellationTokenSource source) in _incomingRequests)
        {
            if (_incomingRequests.TryRemove(requestId, out _))
            {
                source.Cancel();
            }
        }
    }

    private async Task DispatchNotificationAsync(
        string method,
        JSObject? parameterObject,
        string? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialized":
                await _target.InitializedAsync(new InitializedParams(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "exit":
                await _target.ExitAsync().ConfigureAwait(false);
                return;
            case "workspace/didChangeConfiguration":
                await InvokeNotificationAsync<DidChangeConfigurationParams>(
                    parameters,
                    _target.DidChangeConfigurationAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "workspace/didChangeWatchedFiles":
                await InvokeNotificationAsync<DidChangeWatchedFilesParams>(
                    parameters,
                    _target.DidChangeWatchedFilesAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "workspace/didChangeWorkspaceFolders":
                await InvokeNotificationAsync<DidChangeWorkspaceFoldersParams>(
                    parameters,
                    _target.DidChangeWorkspaceFoldersAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "workspace/didCreateFiles":
                await InvokeNotificationAsync<CreateFilesParams>(
                    parameters,
                    _target.DidCreateFilesAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "workspace/didRenameFiles":
                await InvokeNotificationAsync<RenameFilesParams>(
                    parameters,
                    _target.DidRenameFilesAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "workspace/didDeleteFiles":
                await InvokeNotificationAsync<DeleteFilesParams>(
                    parameters,
                    _target.DidDeleteFilesAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didOpen":
                await _target.DidOpenAsync(
                    BrowserLspParameterReader.ReadDidOpen(parameterObject),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didChange":
                await InvokeNotificationAsync<DidChangeTextDocumentParams>(
                    parameters,
                    _target.DidChangeAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didClose":
                await InvokeNotificationAsync<DidCloseTextDocumentParams>(
                    parameters,
                    _target.DidCloseAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didSave":
                await InvokeNotificationAsync<DidSaveTextDocumentParams>(
                    parameters,
                    _target.DidSaveAsync,
                    cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task DispatchRequestAsync(
        string method,
        JSObject? parameterObject,
        string? parameters,
        string requestId,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await InvokeInitializeAsync(parameters, requestId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "shutdown":
                await SendResultAsync(
                    requestId,
                    await _target.ShutdownAsync(cancellationToken).ConfigureAwait(false),
                    typeof(object),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "$/csharp/debugInfo":
                await SendResultAsync(
                    requestId,
                    await _target.GetDebugInfoAsync(cancellationToken).ConfigureAwait(false),
                    typeof(CSharpDebugInfo),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/willSaveWaitUntil":
                await InvokeRequestAsync<WillSaveTextDocumentParams, IReadOnlyList<TextEdit>>(
                    parameters, requestId, _target.WillSaveWaitUntilAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/diagnostic":
                await InvokeRequestAsync<DocumentDiagnosticParams, DocumentDiagnosticReport>(
                    parameters, requestId, _target.DocumentDiagnosticAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "workspace/diagnostic":
                await InvokeRequestAsync<WorkspaceDiagnosticParams, WorkspaceDiagnosticReport>(
                    parameters, requestId, _target.WorkspaceDiagnosticAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/completion":
                await InvokeRequestAsync<CompletionParams, CompletionList>(
                    parameters, requestId, _target.CompletionAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "completionItem/resolve":
                await InvokeRequestAsync<CompletionItem, CompletionItem>(
                    parameters, requestId, _target.CompletionResolveAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/definition":
                await InvokeRequestAsync<TextDocumentPositionParams, IReadOnlyList<Location>>(
                    parameters, requestId, _target.DefinitionAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/declaration":
                await InvokeRequestAsync<TextDocumentPositionParams, IReadOnlyList<Location>>(
                    parameters, requestId, _target.DeclarationAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/typeDefinition":
                await InvokeRequestAsync<TextDocumentPositionParams, IReadOnlyList<Location>>(
                    parameters, requestId, _target.TypeDefinitionAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/implementation":
                await InvokeRequestAsync<TextDocumentPositionParams, IReadOnlyList<Location>>(
                    parameters, requestId, _target.ImplementationAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/selectionRange":
                await InvokeRequestAsync<SelectionRangeParams, IReadOnlyList<SelectionRange>>(
                    parameters, requestId, _target.SelectionRangeAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/foldingRange":
                await InvokeRequestAsync<FoldingRangeParams, IReadOnlyList<FoldingRange>>(
                    parameters, requestId, _target.FoldingRangeAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/linkedEditingRange":
                await InvokeRequestAsync<LinkedEditingRangeParams, LinkedEditingRanges?>(
                    parameters, requestId, _target.LinkedEditingRangeAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/documentHighlight":
                await InvokeRequestAsync<TextDocumentPositionParams, IReadOnlyList<DocumentHighlight>>(
                    parameters, requestId, _target.DocumentHighlightAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/documentLink":
                await InvokeRequestAsync<DocumentLinkParams, IReadOnlyList<DocumentLink>>(
                    parameters, requestId, _target.DocumentLinkAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/moniker":
                await InvokeRequestAsync<MonikerParams, IReadOnlyList<Moniker>>(
                    parameters, requestId, _target.MonikerAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "csharp/metadata":
                await InvokeRequestAsync<CSharpMetadataParams, CSharpMetadataResponse?>(
                    parameters, requestId, _target.CSharpMetadataAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/semanticTokens/full":
                await InvokeRequestAsync<SemanticTokensParams, SemanticTokens>(
                    parameters, requestId, _target.SemanticTokensFullAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/semanticTokens/full/delta":
                await InvokeRequestAsync<SemanticTokensDeltaParams, SemanticTokensDeltaResult>(
                    parameters, requestId, _target.SemanticTokensFullDeltaAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/prepareCallHierarchy":
                await InvokeRequestAsync<CallHierarchyPrepareParams, IReadOnlyList<CallHierarchyItem>>(
                    parameters, requestId, _target.PrepareCallHierarchyAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "callHierarchy/incomingCalls":
                await InvokeRequestAsync<CallHierarchyIncomingCallsParams, IReadOnlyList<CallHierarchyIncomingCall>>(
                    parameters, requestId, _target.CallHierarchyIncomingCallsAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "callHierarchy/outgoingCalls":
                await InvokeRequestAsync<CallHierarchyOutgoingCallsParams, IReadOnlyList<CallHierarchyOutgoingCall>>(
                    parameters, requestId, _target.CallHierarchyOutgoingCallsAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/prepareTypeHierarchy":
                await InvokeRequestAsync<TypeHierarchyPrepareParams, IReadOnlyList<TypeHierarchyItem>>(
                    parameters, requestId, _target.PrepareTypeHierarchyAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "typeHierarchy/supertypes":
                await InvokeRequestAsync<TypeHierarchySupertypesParams, IReadOnlyList<TypeHierarchyItem>>(
                    parameters, requestId, _target.TypeHierarchySupertypesAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "typeHierarchy/subtypes":
                await InvokeRequestAsync<TypeHierarchySubtypesParams, IReadOnlyList<TypeHierarchyItem>>(
                    parameters, requestId, _target.TypeHierarchySubtypesAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/inlayHint":
                await InvokeRequestAsync<InlayHintParams, IReadOnlyList<InlayHint>>(
                    parameters, requestId, _target.InlayHintAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "inlayHint/resolve":
                await InvokeRequestAsync<InlayHint, InlayHint>(
                    parameters, requestId, _target.InlayHintResolveAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/references":
                await InvokeRequestAsync<ReferenceParams, IReadOnlyList<Location>>(
                    parameters, requestId, _target.ReferencesAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/hover":
                await InvokeHoverAsync(parameterObject, requestId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/documentSymbol":
                await InvokeRequestAsync<DocumentSymbolParams, IReadOnlyList<DocumentSymbol>>(
                    parameters, requestId, _target.DocumentSymbolAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "workspace/symbol":
                await InvokeRequestAsync<WorkspaceSymbolParams, IReadOnlyList<WorkspaceSymbol>>(
                    parameters, requestId, _target.WorkspaceSymbolAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "workspaceSymbol/resolve":
                await InvokeRequestAsync<WorkspaceSymbol, WorkspaceSymbol>(
                    parameters, requestId, _target.WorkspaceSymbolResolveAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/signatureHelp":
                await InvokeRequestAsync<SignatureHelpParams, SignatureHelp?>(
                    parameters, requestId, _target.SignatureHelpAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/prepareRename":
                await InvokeRequestAsync<TextDocumentPositionParams, PrepareRenameResult?>(
                    parameters, requestId, _target.PrepareRenameAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/rename":
                await InvokeRequestAsync<RenameParams, WorkspaceEdit>(
                    parameters, requestId, _target.RenameAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/formatting":
                await InvokeRequestAsync<DocumentFormattingParams, IReadOnlyList<TextEdit>>(
                    parameters, requestId, _target.FormattingAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/rangeFormatting":
                await InvokeRequestAsync<DocumentRangeFormattingParams, IReadOnlyList<TextEdit>>(
                    parameters, requestId, _target.RangeFormattingAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/onTypeFormatting":
                await InvokeRequestAsync<DocumentOnTypeFormattingParams, IReadOnlyList<TextEdit>>(
                    parameters, requestId, _target.OnTypeFormattingAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "textDocument/codeAction":
                await InvokeRequestAsync<CodeActionParams, IReadOnlyList<CodeAction>>(
                    parameters, requestId, _target.CodeActionAsync, cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                await SendErrorAsync(
                    requestId,
                    MethodNotFoundCode,
                    $"The method is not implemented: {method}",
                    cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task InvokeNotificationAsync<TParameters>(
        string? parameters,
        Func<TParameters, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        TParameters value = Deserialize<TParameters>(parameters);
        await handler(value, cancellationToken).ConfigureAwait(false);
    }

    private async Task InvokeInitializeAsync(
        string? parameters,
        string requestId,
        CancellationToken cancellationToken)
    {
        InitializeParams value = Deserialize<InitializeParams>(parameters);
        InitializeResult result = await _target.InitializeAsync(value, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool supportsRefactor = result.Capabilities.CodeActionProvider is not null &&
            result.Capabilities.CodeActionProvider.CodeActionKinds.Count == 3;
        BrowserLanguageServerHost.SendInitializeResult(
            requestId,
            supportsRefactor,
            result.ServerInfo.Version);
    }

    private async Task InvokeHoverAsync(
        JSObject? parameters,
        string requestId,
        CancellationToken cancellationToken)
    {
        TextDocumentPositionParams value =
            BrowserLspParameterReader.ReadTextDocumentPosition(parameters);
        Hover? result = await _target.HoverAsync(value, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LspRange? range = result?.Range;
        BrowserLanguageServerHost.SendHoverResult(
            requestId,
            result is not null,
            result?.Contents.Kind,
            result?.Contents.Value,
            range is not null,
            range?.Start.Line ?? 0,
            range?.Start.Character ?? 0,
            range?.End.Line ?? 0,
            range?.End.Character ?? 0);
    }

    private async Task InvokeRequestAsync<TParameters, TResult>(
        string? parameters,
        string requestId,
        Func<TParameters, CancellationToken, Task<TResult>> handler,
        CancellationToken cancellationToken)
    {
        TParameters value = Deserialize<TParameters>(parameters);
        TResult result = await handler(value, cancellationToken).ConfigureAwait(false);
        await SendResultAsync(requestId, result, typeof(TResult), cancellationToken)
            .ConfigureAwait(false);
    }

    private TParameters Deserialize<TParameters>(string? parameters)
    {
        if (parameters is null)
        {
            throw new JsonException("The JSON-RPC request requires a parameter object.");
        }

        return (TParameters)(JsonSerializer.Deserialize(
            parameters,
            _serializerOptions.GetTypeInfo(typeof(TParameters)))
            ?? throw new JsonException("The JSON-RPC parameter object was null."));
    }

    private ValueTask SendResultAsync(
        string requestId,
        object? result,
        Type resultType,
        CancellationToken cancellationToken) =>
        SendAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                writer.WriteRawValue(requestId);
                writer.WritePropertyName("result");
                if (result is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(
                        writer,
                        result,
                        _serializerOptions.GetTypeInfo(resultType));
                }

                writer.WriteEndObject();
            },
            cancellationToken);

    private ValueTask SendErrorAsync(
        string requestId,
        int code,
        string message,
        CancellationToken cancellationToken) =>
        SendErrorAsync(requestId, code, message, cancellationData: null, cancellationToken);

    private ValueTask SendErrorAsync(
        string requestId,
        int code,
        string message,
        DiagnosticServerCancellationData? cancellationData,
        CancellationToken cancellationToken) =>
        SendAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                writer.WriteRawValue(requestId);
                writer.WriteStartObject("error");
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                if (cancellationData is not null)
                {
                    writer.WritePropertyName("data");
                    JsonSerializer.Serialize(
                        writer,
                        cancellationData,
                        LspJsonSerializerContext.Default.DiagnosticServerCancellationData);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);

    private ValueTask SendNotificationFailureAsync(string method, Exception exception) =>
        SendAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", "window/logMessage");
                writer.WriteStartObject("params");
                writer.WriteNumber("type", 1);
                writer.WriteString(
                    "message",
                    $"csls could not process {method}: {exception.Message}");
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            CancellationToken.None);

    private static ValueTask SendSerializedResultAsync(
        string requestId,
        string result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrowserLanguageServerHost.SendResult(requestId, result);
        return ValueTask.CompletedTask;
    }

    private async ValueTask SendAsync(
        Action<Utf8JsonWriter> writeMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writeMessage(writer);
        }

        string message = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await _sendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private void CancelRequest(string? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(parameters);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("id", out JsonElement requestId))
        {
            return;
        }

        if (_incomingRequests.TryGetValue(
            requestId.GetRawText(),
            out CancellationTokenSource? source))
        {
            source.Cancel();
        }
    }
}
