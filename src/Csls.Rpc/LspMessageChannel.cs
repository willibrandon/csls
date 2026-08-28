using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using System.Buffers;
using System.Text;
using System.Threading.Channels;

namespace Csls.Rpc;

/// <summary>
/// Transports complete LSP JSON messages through an asynchronous callback and channel.
/// </summary>
public sealed class LspMessageChannel : IJsonRpcMessageHandler, IDisposable
{
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private readonly Channel<JsonRpcMessage> _incomingMessages =
        Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(256)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Func<string, CancellationToken, ValueTask> _sendMessageAsync;
    private readonly LspJsonRpcFormatter _formatter = new(
        LspRpcJson.CreateSerializerOptions());
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposeState;

    /// <summary>
    /// Creates a complete-message LSP transport with one asynchronous output callback.
    /// </summary>
    /// <param name="sendMessageAsync">Writes one complete JSON-RPC message to the peer.</param>
    public LspMessageChannel(Func<string, CancellationToken, ValueTask> sendMessageAsync)
    {
        ArgumentNullException.ThrowIfNull(sendMessageAsync);
        _sendMessageAsync = sendMessageAsync;
    }

    /// <summary>
    /// Gets whether this channel accepts incoming messages.
    /// </summary>
    public bool CanRead => Volatile.Read(ref _disposeState) == 0;

    /// <summary>
    /// Gets whether this channel can send outgoing messages.
    /// </summary>
    public bool CanWrite => Volatile.Read(ref _disposeState) == 0;

    /// <summary>
    /// Gets the source-generated LSP message formatter used by StreamJsonRpc.
    /// </summary>
    public IJsonRpcMessageFormatter Formatter => _formatter;

    /// <summary>
    /// Accepts one complete UTF-16 JSON message from the connected peer.
    /// </summary>
    /// <param name="message">The complete JSON-RPC message.</param>
    /// <param name="cancellationToken">The receive cancellation token.</param>
    /// <returns>A task that completes after the message enters the bounded input queue.</returns>
    public async ValueTask ReceiveAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        int byteCount = Encoding.UTF8.GetByteCount(message);
        if (byteCount > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"LSP payload length {byteCount} exceeds the {MaximumPayloadBytes}-byte limit.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(message);
        JsonRpcMessage rpcMessage = _formatter.Deserialize(new ReadOnlySequence<byte>(bytes));
        await _incomingMessages.Writer.WriteAsync(rpcMessage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the incoming message stream and disconnects the RPC session.
    /// </summary>
    /// <param name="exception">The optional transport failure.</param>
    public void Complete(Exception? exception = null) =>
        _incomingMessages.Writer.TryComplete(exception);

    /// <summary>
    /// Reads the next complete incoming JSON-RPC message.
    /// </summary>
    /// <param name="cancellationToken">The read cancellation token.</param>
    /// <returns>The next message, or null after the peer completes the channel.</returns>
    public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incomingMessages.Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is null)
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes and sends one complete outgoing JSON-RPC message in order.
    /// </summary>
    /// <param name="jsonRpcMessage">The message to send.</param>
    /// <param name="cancellationToken">The write cancellation token.</param>
    /// <returns>A task that completes after the peer accepts the message.</returns>
    public async ValueTask WriteAsync(
        JsonRpcMessage jsonRpcMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonRpcMessage);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            _formatter.Serialize(buffer, jsonRpcMessage);
            string message = Encoding.UTF8.GetString(buffer.WrittenSpan);
            await _sendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Completes the channel and releases its formatter and write synchronization.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _incomingMessages.Writer.TryComplete();
        _formatter.Dispose();
        _writeGate.Dispose();
    }
}
