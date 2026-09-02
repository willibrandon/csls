using Csls.DebugAdapter;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Drives the production DAP session through real anonymous operating-system pipes.
/// </summary>
internal sealed class DapTestClient : IAsyncDisposable
{
    private ConnectedNamedPipePair? _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<int>? _sessionTask;
    private int _sequence;
    private int _disposed;

    /// <summary>
    /// Creates and starts a production DAP session connected through an operating-system pipe.
    /// </summary>
    private DapTestClient()
    {
        Diagnostics = new StringWriter(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Connects the test client and starts a production DAP session.
    /// </summary>
    /// <param name="cancellationToken">Cancels connection establishment.</param>
    /// <returns>The connected test client.</returns>
    internal static async Task<DapTestClient> CreateAsync(CancellationToken cancellationToken)
    {
        var client = new DapTestClient();
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Gets diagnostics emitted outside the DAP protocol stream.
    /// </summary>
    internal StringWriter Diagnostics { get; }

    /// <summary>
    /// Sends one framed DAP request through the client-to-adapter pipe.
    /// </summary>
    /// <param name="command">The request command.</param>
    /// <param name="writeArguments">An optional arguments-object writer.</param>
    /// <param name="cancellationToken">Cancels the pipe write.</param>
    /// <returns>The assigned request sequence number.</returns>
    internal async Task<int> SendRequestAsync(
        string command,
        Action<Utf8JsonWriter>? writeArguments,
        CancellationToken cancellationToken)
    {
        ConnectedNamedPipePair pipe = _pipe ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        int sequence = Interlocked.Increment(ref _sequence);
        ArrayBufferWriter<byte> payload = new();
        using (Utf8JsonWriter writer = new(payload))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            writer.WriteString("type", "request");
            writer.WriteString("command", command);
            if (writeArguments is not null)
            {
                writer.WritePropertyName("arguments");
                writeArguments(writer);
            }

            writer.WriteEndObject();
        }

        byte[] header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"Content-Length: {payload.WrittenCount}\r\n\r\n"));
        await pipe.Client.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await pipe.Client
            .WriteAsync(payload.WrittenMemory, cancellationToken)
            .ConfigureAwait(false);
        await pipe.Client.FlushAsync(cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    /// <summary>
    /// Sends caller-provided protocol bytes, optionally as single-byte writes.
    /// </summary>
    /// <param name="frame">The exact bytes to send.</param>
    /// <param name="fragment">Whether to write and flush one byte at a time.</param>
    /// <param name="cancellationToken">Cancels the pipe write.</param>
    /// <returns>A task that completes after the bytes are flushed.</returns>
    internal async Task SendFrameAsync(
        byte[] frame,
        bool fragment,
        CancellationToken cancellationToken)
    {
        ConnectedNamedPipePair pipe = _pipe ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        if (fragment)
        {
            foreach (byte value in frame)
            {
                byte[] singleByte = [value];
                await pipe.Client
                    .WriteAsync(singleByte, cancellationToken)
                    .ConfigureAwait(false);
                await pipe.Client.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await pipe.Client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.Client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed DAP response or event from the adapter-to-client pipe.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pipe read.</param>
    /// <returns>An owned JSON document containing the message.</returns>
    internal async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        ConnectedNamedPipePair pipe = _pipe ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        List<byte> header = [];
        byte[] oneByte = new byte[1];
        while (header.Count < 8 * 1024)
        {
            int count = await pipe.Client
                .ReadAsync(oneByte, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The DAP response stream ended in a header.");
            }

            header.Add(oneByte[0]);
            if (header.Count >= 4 &&
                header[^4] == (byte)'\r' &&
                header[^3] == (byte)'\n' &&
                header[^2] == (byte)'\r' &&
                header[^1] == (byte)'\n')
            {
                break;
            }
        }

        string headerText = Encoding.ASCII.GetString([.. header]);
        string lengthText = headerText["Content-Length: ".Length..^4];
        int payloadLength = int.Parse(lengthText, CultureInfo.InvariantCulture);
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await pipe.Client.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    /// <summary>
    /// Waits for the production DAP session to finish.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The session exit code.</returns>
    internal Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        Task<int> sessionTask = _sessionTask ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        return sessionTask.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Closes the client protocol endpoint so the adapter observes end of stream.
    /// </summary>
    /// <returns>A task that completes after the endpoint closes.</returns>
    internal ValueTask CloseProtocolAsync()
    {
        ConnectedNamedPipePair pipe = _pipe ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        return pipe.Client.DisposeAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_sessionTask is not null)
        {
            _ = await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
        Diagnostics.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _pipe = await ConnectedNamedPipePair
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);
        _sessionTask = DebugAdapterHost.RunAsync(
            _pipe.Server,
            _pipe.Server,
            Diagnostics,
            _lifetime.Token);
    }
}
