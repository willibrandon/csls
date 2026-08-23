using System.Buffers;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Csls.Rpc;

/// <summary>
/// Normalizes LSP parameter objects before delegating to the StreamJsonRpc JSON formatter.
/// </summary>
internal sealed class LspJsonRpcFormatter :
    IJsonRpcMessageTextFormatter,
    IJsonRpcInstanceContainer,
    IJsonRpcMessageFactory,
    IJsonRpcFormatterTracingCallbacks,
    IDisposable
{
    private const string ParametersPropertyName = "params";
    private readonly SystemTextJsonFormatter _formatter;

    /// <summary>
    /// Creates an LSP formatter backed by the supplied source-generated serializer options.
    /// </summary>
    /// <param name="serializerOptions">The LSP serializer options.</param>
    internal LspJsonRpcFormatter(JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        LspRpcJson.Configure(serializerOptions);
        _formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = serializerOptions
        };
    }

    /// <summary>
    /// Gets or sets the JSON message encoding used by the underlying formatter.
    /// </summary>
    public Encoding Encoding
    {
        get => _formatter.Encoding;
        set => _formatter.Encoding = value;
    }

    JsonRpc IJsonRpcInstanceContainer.Rpc
    {
        set => ((IJsonRpcInstanceContainer)_formatter).Rpc = value;
    }

    /// <summary>
    /// Deserializes one UTF-8 JSON-RPC message after normalizing null LSP parameters.
    /// </summary>
    /// <param name="contentBuffer">The complete message content.</param>
    /// <returns>The deserialized JSON-RPC message.</returns>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        byte[]? normalized = NormalizeNullParameters(contentBuffer, Encoding);
        return normalized is null
            ? _formatter.Deserialize(contentBuffer)
            : _formatter.Deserialize(new ReadOnlySequence<byte>(normalized));
    }

    /// <summary>
    /// Deserializes one JSON-RPC message with its explicitly selected text encoding.
    /// </summary>
    /// <param name="contentBuffer">The complete message content.</param>
    /// <param name="encoding">The message text encoding.</param>
    /// <returns>The deserialized JSON-RPC message.</returns>
    public JsonRpcMessage Deserialize(
        ReadOnlySequence<byte> contentBuffer,
        Encoding encoding)
    {
        byte[]? normalized = NormalizeNullParameters(contentBuffer, encoding);
        return normalized is null
            ? _formatter.Deserialize(contentBuffer, encoding)
            : _formatter.Deserialize(new ReadOnlySequence<byte>(normalized), encoding);
    }

    /// <summary>
    /// Serializes one JSON-RPC message through the configured System.Text.Json formatter.
    /// </summary>
    /// <param name="bufferWriter">The destination byte writer.</param>
    /// <param name="message">The JSON-RPC message.</param>
    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message) =>
        _formatter.Serialize(bufferWriter, message);

    /// <summary>
    /// Releases the delegated StreamJsonRpc formatter and its retained JSON buffers.
    /// </summary>
    public void Dispose() => _formatter.Dispose();

    object IJsonRpcMessageFormatter.GetJsonText(JsonRpcMessage message) =>
        throw new NotSupportedException("Legacy formatter tracing is not supported.");

    JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() =>
        ((IJsonRpcMessageFactory)_formatter).CreateRequestMessage();

    JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() =>
        ((IJsonRpcMessageFactory)_formatter).CreateErrorMessage();

    JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() =>
        ((IJsonRpcMessageFactory)_formatter).CreateResultMessage();

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(
        JsonRpcMessage message,
        ReadOnlySequence<byte> encodedMessage) =>
        ((IJsonRpcFormatterTracingCallbacks)_formatter).OnSerializationComplete(
            message,
            encodedMessage);

    private static byte[]? NormalizeNullParameters(
        ReadOnlySequence<byte> contentBuffer,
        Encoding encoding)
    {
        if (encoding.CodePage != Encoding.UTF8.CodePage)
        {
            return null;
        }

        using var document = JsonDocument.Parse(contentBuffer);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(ParametersPropertyName, out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(checked((int)contentBuffer.Length));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!property.NameEquals(ParametersPropertyName))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
