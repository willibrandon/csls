using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Serves exact source bytes across a real loopback HTTP transport.
/// </summary>
internal sealed class SourceLinkTestServer : IAsyncDisposable
{
    private readonly byte[] _content;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private Task? _serverTask;
    private int _requestCount;

    /// <summary>
    /// Creates a Source Link test server for exact source bytes.
    /// </summary>
    /// <param name="content">The bytes returned by every source request.</param>
    internal SourceLinkTestServer(byte[] content)
    {
        _content = content;
    }

    /// <summary>
    /// Gets the Source Link URL pattern for the listening endpoint.
    /// </summary>
    internal string SourceLinkPattern { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the number of HTTP requests accepted by the server.
    /// </summary>
    internal int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Starts the loopback listener.
    /// </summary>
    internal void Start()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        SourceLinkPattern = $"http://127.0.0.1:{endpoint.Port}/sources/*";
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
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
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
        while (!string.IsNullOrEmpty(
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }

        _ = Interlocked.Increment(ref _requestCount);
        byte[] header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Length: {_content.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Content-Type: text/x-csharp\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
    }
}
