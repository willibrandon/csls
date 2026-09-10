using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Serves one exact PDB across a real loopback symbol-server transport.
/// </summary>
internal sealed class SymbolServerTestServer : IAsyncDisposable
{
    private readonly byte[] _content;
    private readonly string _expectedTarget;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private Task? _serverTask;
    private int _requestCount;

    /// <summary>
    /// Creates a server for one symbol-store request target and exact response body.
    /// </summary>
    /// <param name="expectedTarget">The absolute HTTP request target.</param>
    /// <param name="content">The Portable PDB bytes returned for that target.</param>
    internal SymbolServerTestServer(string expectedTarget, byte[] content)
    {
        _expectedTarget = expectedTarget;
        _content = content;
    }

    /// <summary>
    /// Gets the absolute loopback symbol-server base URL.
    /// </summary>
    internal string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the total number of accepted HTTP requests.
    /// </summary>
    internal int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Starts the loopback listener.
    /// </summary>
    internal void Start()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/symbols/";
        _serverTask = RunAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener.Dispose();
        if (_serverTask is not null)
        {
            await _serverTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                using (client)
                {
                    await WriteResponseAsync(client, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task WriteResponseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        string requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? string.Empty;
        while (true)
        {
            string? headerLine = await reader.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
            {
                break;
            }
        }

        _ = Interlocked.Increment(ref _requestCount);
        string target = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ElementAtOrDefault(1) ?? string.Empty;
        bool found = string.Equals(
            target,
            $"/symbols/{_expectedTarget.TrimStart('/')}",
            StringComparison.OrdinalIgnoreCase);
        byte[] body = found ? _content : [];
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
