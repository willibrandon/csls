using Csls.Protocol;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Csls.Web.Worker;

/// <summary>
/// Sends typed server-to-client LSP messages over the browser worker bridge.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserLspClientConnection : ILspClientConnection, IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>>
        _pendingRequests = new();
    private readonly Func<string, CancellationToken, ValueTask> _sendMessageAsync;
    private readonly JsonSerializerOptions _serializerOptions = LspJson.CreateSerializerOptions();
    private long _nextRequestId;
    private int _disposeState;

    /// <summary>
    /// Creates a browser client connection over one complete-message callback.
    /// </summary>
    /// <param name="sendMessageAsync">Sends one complete JSON-RPC message to JavaScript.</param>
    internal BrowserLspClientConnection(
        Func<string, CancellationToken, ValueTask> sendMessageAsync)
    {
        ArgumentNullException.ThrowIfNull(sendMessageAsync);
        _sendMessageAsync = sendMessageAsync;
    }

    /// <inheritdoc />
    public Task<JsonElement?[]> GetConfigurationAsync(
        ConfigurationParams parameters,
        CancellationToken cancellationToken) =>
        SendRequestAsync<ConfigurationParams, JsonElement?[]>(
            "workspace/configuration",
            parameters,
            cancellationToken);

    /// <inheritdoc />
    public async Task CreateWorkDoneProgressAsync(
        WorkDoneProgressCreateParams parameters,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync<WorkDoneProgressCreateParams, object?>(
            "window/workDoneProgress/create",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PublishWorkDoneProgressAsync(WorkDoneProgressParams parameters) =>
        SendNotificationAsync("$/progress", parameters);

    /// <inheritdoc />
    public Task PublishWorkspaceDiagnosticProgressAsync(
        WorkspaceDiagnosticProgressParams parameters) =>
        SendNotificationAsync("$/progress", parameters);

    /// <inheritdoc />
    public Task PublishDiagnosticsAsync(PublishDiagnosticsParams parameters) =>
        SendNotificationAsync("textDocument/publishDiagnostics", parameters);

    /// <inheritdoc />
    public async Task RegisterCapabilityAsync(
        RegistrationParams parameters,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync<RegistrationParams, object?>(
            "client/registerCapability",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync<object?>(
            "workspace/diagnostic/refresh",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshInlayHintsAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync<object?>(
            "workspace/inlayHint/refresh",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes one pending server-to-client request from a browser client response.
    /// </summary>
    /// <param name="requestId">The serialized response identifier.</param>
    /// <param name="result">The serialized response result, when present.</param>
    /// <param name="error">The serialized response error, when present.</param>
    internal void AcceptResponse(
        string? requestId,
        string? result,
        string? error)
    {
        if (!long.TryParse(requestId, out long numericRequestId) ||
            !_pendingRequests.TryRemove(
                numericRequestId,
                out TaskCompletionSource<string?>? pending))
        {
            return;
        }

        if (error is not null)
        {
            using var errorDocument = JsonDocument.Parse(error);
            JsonElement errorElement = errorDocument.RootElement;
            string message = errorElement.TryGetProperty("message", out JsonElement errorMessage)
                ? errorMessage.GetString() ?? "The LSP client returned an error."
                : "The LSP client returned an error.";
            pending.TrySetException(new InvalidOperationException(message));
            return;
        }

        pending.TrySetResult(result);
    }

    /// <summary>
    /// Fails pending requests after the browser transport is closed.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var exception = new ObjectDisposedException(nameof(BrowserLspClientConnection));
        foreach ((long requestId, TaskCompletionSource<string?> pending) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(requestId, out _))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private async Task<TResult> SendRequestAsync<TParameters, TResult>(
        string method,
        TParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        long requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException($"Duplicate browser LSP request ID: {requestId}");
        }

        try
        {
            await SendAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteNumber("id", requestId);
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(
                        writer,
                        parameters,
                        _serializerOptions.GetTypeInfo(typeof(TParameters)));
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
            string? result = await completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is null || string.Equals(result, "null", StringComparison.Ordinal))
            {
                return default!;
            }

            return (TResult)(JsonSerializer.Deserialize(
                result,
                _serializerOptions.GetTypeInfo(typeof(TResult)))
                ?? throw new JsonException($"The {method} result was null."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pendingRequests.TryRemove(requestId, out _))
            {
                await SendCancellationAsync(requestId).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task<TResult> SendRequestAsync<TResult>(
        string method,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        long requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException($"Duplicate browser LSP request ID: {requestId}");
        }

        try
        {
            await SendAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteNumber("id", requestId);
                    writer.WriteString("method", method);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
            string? result = await completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is null || string.Equals(result, "null", StringComparison.Ordinal))
            {
                return default!;
            }

            return (TResult)(JsonSerializer.Deserialize(
                result,
                _serializerOptions.GetTypeInfo(typeof(TResult)))
                ?? throw new JsonException($"The {method} result was null."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pendingRequests.TryRemove(requestId, out _))
            {
                await SendCancellationAsync(requestId).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private Task SendNotificationAsync<TParameters>(
        string method,
        TParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return SendAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(
                    writer,
                    parameters,
                    _serializerOptions.GetTypeInfo(typeof(TParameters)));
                writer.WriteEndObject();
            },
            CancellationToken.None).AsTask();
    }

    private ValueTask SendCancellationAsync(long requestId) =>
        SendAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", "$/cancelRequest");
                writer.WriteStartObject("params");
                writer.WriteNumber("id", requestId);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            CancellationToken.None);

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
}
