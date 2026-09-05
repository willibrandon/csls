using Csls.DebugAdapter.Protocol;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Serializes Debug Adapter Protocol responses and events without interleaving.
/// </summary>
internal sealed class DapMessageWriter : IAsyncDisposable
{
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _sequence;

    /// <summary>
    /// Creates a serialized DAP message writer.
    /// </summary>
    /// <param name="output">The adapter-to-client byte stream.</param>
    internal DapMessageWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// Writes a response for a client request.
    /// </summary>
    /// <param name="request">The request being answered.</param>
    /// <param name="success">Whether the request succeeded.</param>
    /// <param name="message">An optional failure description.</param>
    /// <param name="writeBody">An optional response-body writer.</param>
    /// <param name="cancellationToken">Cancels the stream write.</param>
    /// <returns>A task that completes after the framed response is flushed.</returns>
    internal ValueTask WriteResponseAsync(
        Request request,
        bool success,
        string? message,
        Action<Utf8JsonWriter>? writeBody,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("seq", Interlocked.Increment(ref _sequence));
                writer.WriteString("type", "response");
                writer.WriteNumber("request_seq", request.Seq);
                writer.WriteBoolean("success", success);
                writer.WriteString("command", request.Command);
                if (message is not null)
                {
                    writer.WriteString("message", message);
                }

                if (writeBody is not null)
                {
                    writer.WritePropertyName("body");
                    writeBody(writer);
                }

                writer.WriteEndObject();
            },
            cancellationToken);

    /// <summary>
    /// Writes a server-originated DAP event.
    /// </summary>
    /// <param name="eventName">The DAP event name.</param>
    /// <param name="writeBody">An optional event-body writer.</param>
    /// <param name="cancellationToken">Cancels the stream write.</param>
    /// <returns>A task that completes after the framed event is flushed.</returns>
    internal ValueTask WriteEventAsync(
        string eventName,
        Action<Utf8JsonWriter>? writeBody,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("seq", Interlocked.Increment(ref _sequence));
                writer.WriteString("type", "event");
                writer.WriteString("event", eventName);
                if (writeBody is not null)
                {
                    writer.WritePropertyName("body");
                    writeBody(writer);
                }

                writer.WriteEndObject();
            },
            cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return _output.DisposeAsync();
    }

    private async ValueTask WriteMessageAsync(
        Action<Utf8JsonWriter> writeMessage,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArrayBufferWriter<byte> payload = new();
            using (Utf8JsonWriter writer = new(payload))
            {
                writeMessage(writer);
            }

            string headerText = string.Create(
                CultureInfo.InvariantCulture,
                $"Content-Length: {payload.WrittenCount}\r\n\r\n");
            byte[] header = Encoding.ASCII.GetBytes(headerText);

            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(payload.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
