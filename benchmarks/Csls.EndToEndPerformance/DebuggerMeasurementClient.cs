using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Owns a measured debugger process and its bounded, independently parsed DAP transport.
/// </summary>
internal sealed class DebuggerMeasurementClient : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8192;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private readonly Process _process;
    private Task _diagnosticsTask = Task.CompletedTask;
    private readonly List<JsonElement> _events = [];
    private readonly Dictionary<int, JsonElement> _responses = [];
    private readonly StringBuilder _output = new();
    private string _diagnostics = string.Empty;
    private int _requestSequence;
    private int _receivedSequence;
    private bool _started;

    private DebuggerMeasurementClient(Process process, long startedTimestamp)
    {
        _process = process;
        StartedTimestamp = startedTimestamp;
    }

    /// <summary>
    /// Gets the monotonic timestamp immediately before process creation.
    /// </summary>
    internal long StartedTimestamp { get; }

    /// <summary>
    /// Gets the operating-system creation time of the measured launcher.
    /// </summary>
    internal DateTimeOffset StartedAtUtc { get; private set; }

    /// <summary>
    /// Gets the measured launcher process identifier.
    /// </summary>
    internal int ProcessId => _process.Id;

    /// <summary>
    /// Gets the process identifier announced by the target's DAP event.
    /// </summary>
    internal int TargetProcessId { get; private set; }

    /// <summary>
    /// Gets the target exit code announced through DAP.
    /// </summary>
    internal int? TargetExitCode { get; private set; }

    /// <summary>
    /// Gets the bounded output received through target output events.
    /// </summary>
    internal string Output => _output.ToString();

    /// <summary>
    /// Starts the selected launcher with real redirected protocol streams.
    /// </summary>
    /// <param name="serverPath">The native executable or managed launcher assembly.</param>
    /// <returns>The exclusively owned measurement connection.</returns>
    internal static async Task<DebuggerMeasurementClient> StartAsync(string serverPath)
    {
        bool managed = serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed
            ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : serverPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (managed)
        {
            start.ArgumentList.Add(serverPath);
        }
        start.ArgumentList.Add("debugger");
        start.ArgumentList.Add("dap");
        var client = new DebuggerMeasurementClient(new Process { StartInfo = start }, Stopwatch.GetTimestamp());
        try
        {
            if (!client._process.Start())
            {
                throw new InvalidOperationException("The measured debugger did not start.");
            }
            client._started = true;
            client._diagnosticsTask = client.CaptureDiagnosticsAsync();
            client.StartedAtUtc = client._process.StartTime.ToUniversalTime();
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends one request and receives its matching successful response.
    /// </summary>
    /// <param name="command">The DAP command name.</param>
    /// <param name="writeArguments">Writes the complete arguments object.</param>
    /// <param name="cancellationToken">Cancels this measured operation.</param>
    /// <returns>The response body, or an undefined element when no body is present.</returns>
    internal async Task<JsonElement> RequestAsync(string command, Action<Utf8JsonWriter>? writeArguments,
        CancellationToken cancellationToken)
    {
        int sequence = await SendAsync(command, writeArguments, cancellationToken).ConfigureAwait(false);
        return await ResponseAsync(sequence, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request whose completion can follow client configuration.
    /// </summary>
    /// <param name="command">The DAP command name.</param>
    /// <param name="writeArguments">Writes the complete arguments object.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The request's correlation identifier.</returns>
    internal async Task<int> SendAsync(string command, Action<Utf8JsonWriter>? writeArguments,
        CancellationToken cancellationToken)
    {
        int sequence = checked(++_requestSequence);
        var payload = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            writer.WriteString("type", "request");
            writer.WriteString("command", command);
            writer.WritePropertyName("arguments");
            if (writeArguments is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                writeArguments(writer);
            }
            writer.WriteEndObject();
        }
        byte[] header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"Content-Length: {payload.WrittenCount}\r\n\r\n"));
        await _process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.WriteAsync(payload.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    /// <summary>
    /// Receives the successful response for an already sent request while preserving intervening events.
    /// </summary>
    /// <param name="sequence">The requested correlation identifier.</param>
    /// <param name="command">The expected command name.</param>
    /// <param name="cancellationToken">Cancels the response wait.</param>
    /// <returns>The response body.</returns>
    internal async Task<JsonElement> ResponseAsync(int sequence, string command, CancellationToken cancellationToken)
    {
        while (!_responses.ContainsKey(sequence))
        {
            await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        JsonElement response = _responses[sequence];
        _responses.Remove(sequence);
        if (response.GetProperty("command").GetString() != command || !response.GetProperty("success").GetBoolean())
        {
            throw new InvalidDataException($"Measured DAP request {command} failed: {response}");
        }
        return response.TryGetProperty("body", out JsonElement body) ? body : default;
    }

    /// <summary>
    /// Receives a named event while preserving interleaved responses and other events.
    /// </summary>
    /// <param name="name">The expected event name.</param>
    /// <param name="cancellationToken">Cancels the event wait.</param>
    /// <returns>The matched event body.</returns>
    internal async Task<JsonElement> EventAsync(string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            int index = _events.FindIndex(item => item.GetProperty("event").GetString() == name);
            if (index >= 0)
            {
                JsonElement result = _events[index];
                _events.RemoveAt(index);
                return result.TryGetProperty("body", out JsonElement body) ? body : default;
            }
            await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes launcher exit and checks its independently drained diagnostics.
    /// </summary>
    /// <param name="cancellationToken">Cancels the exit wait.</param>
    /// <returns>The observed exit code.</returns>
    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await _diagnosticsTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        if (_process.ExitCode != 0 || !string.IsNullOrWhiteSpace(_diagnostics))
        {
            throw new InvalidDataException($"Measured debugger exited with {_process.ExitCode}: {_diagnostics}");
        }
        return _process.ExitCode;
    }

    /// <summary>
    /// Closes the protocol and retires the exclusively owned process tree on every exit path.
    /// </summary>
    /// <returns>A task that completes after process and stream cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        using Process process = _process;
        if (!_started)
        {
            return;
        }
        process.StandardInput.Close();
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await _diagnosticsTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[MaximumHeaderBytes];
        int length = 0;
        while (length < header.Length)
        {
            int read = await _process.StandardOutput.BaseStream.ReadAsync(header.AsMemory(length, 1), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"The measured debugger closed DAP unexpectedly. Diagnostics: {_diagnostics}");
            }
            if (header[length++] > 127)
            {
                throw new InvalidDataException("A measured DAP header contains non-ASCII bytes.");
            }
            if (length >= 4 && header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                break;
            }
        }
        string text = Encoding.ASCII.GetString(header, 0, length);
        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            !text.StartsWith("Content-Length: ", StringComparison.Ordinal) ||
            !int.TryParse(text.AsSpan("Content-Length: ".Length, length - "Content-Length: ".Length - 4),
                NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The measured DAP response has an invalid or oversized header.");
        }
        byte[] payload = GC.AllocateUninitializedArray<byte>(count);
        await _process.StandardOutput.BaseStream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        JsonElement message = document.RootElement;
        int sequence = message.GetProperty("seq").GetInt32();
        if (sequence <= _receivedSequence)
        {
            throw new InvalidDataException("Measured DAP sequence numbers did not increase.");
        }
        _receivedSequence = sequence;
        if (message.GetProperty("type").GetString() == "response")
        {
            if (_responses.Count >= 8 || !_responses.TryAdd(message.GetProperty("request_seq").GetInt32(), message.Clone()))
            {
                throw new InvalidDataException("The measured debugger returned excessive or duplicate responses.");
            }
            return;
        }
        if (message.GetProperty("type").GetString() != "event")
        {
            throw new InvalidDataException("The measured debugger returned an unexpected message type.");
        }
        string? name = message.GetProperty("event").GetString();
        if (name == "output")
        {
            string? output = message.GetProperty("body").GetProperty("output").GetString();
            if (output is not null && output.Length > 65536 - _output.Length)
            {
                throw new InvalidDataException("The measurement target exceeded its output budget.");
            }
            _output.Append(output);
            return;
        }
        if (name == "process")
        {
            TargetProcessId = message.GetProperty("body").GetProperty("systemProcessId").GetInt32();
        }
        else if (name == "exited")
        {
            TargetExitCode = message.GetProperty("body").GetProperty("exitCode").GetInt32();
        }
        if (_events.Count >= 256)
        {
            throw new InvalidDataException("The measured debugger exceeded its pending event budget.");
        }
        _events.Add(message.Clone());
    }

    private async Task CaptureDiagnosticsAsync()
    {
        char[] buffer = new char[1024];
        while (true)
        {
            int count = await _process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }
            string combined = _diagnostics + new string(buffer, 0, count);
            _diagnostics = combined[^Math.Min(8192, combined.Length)..];
        }
    }
}
