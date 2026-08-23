using Csls.Control.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Csls.Control;

/// <summary>
/// Hosts bounded versioned StreamJsonRpc control connections over a private Unix-domain socket.
/// </summary>
public sealed partial class ControlRpcServer : IHostedService, IAsyncDisposable
{
    private const int MaximumConnections = 16;
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly IControlRpcTarget _target;
    private readonly ILogger<ControlRpcServer> _logger;
    private readonly Channel<Socket> _connections = Channel.CreateBounded<Socket>(
        new BoundedChannelOptions(MaximumConnections)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
    private CancellationTokenSource? _shutdownSource;
    private Socket? _listener;
    private Task? _acceptTask;
    private Task[] _connectionWorkers = [];
    private string? _socketPath;
    private int _disposeState;

    /// <summary>
    /// Creates the hosted control socket for the current language-server session.
    /// </summary>
    /// <param name="target">The explicitly registered control target.</param>
    /// <param name="logger">The structured control logger.</param>
    public ControlRpcServer(
        IControlRpcTarget target,
        ILogger<ControlRpcServer> logger)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);
        _target = target;
        _logger = logger;
    }

    /// <summary>
    /// Binds the private session socket before the worker begins accepting LSP requests.
    /// </summary>
    /// <param name="cancellationToken">The host startup cancellation token.</param>
    /// <returns>A completed task after the socket is listening.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The control server has already started.");
        }

        _socketPath = ControlEndpoint.PrepareSocketPath(Environment.ProcessId);
        _shutdownSource = new CancellationTokenSource();
        _listener = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(MaximumConnections);
        ControlEndpoint.RestrictSocket(_socketPath);
        _connectionWorkers =
        [
            .. Enumerable
                .Range(0, MaximumConnections)
                .Select(_ => RunConnectionWorkerAsync(_shutdownSource.Token))
        ];
        _acceptTask = AcceptConnectionsAsync(_shutdownSource.Token);
        LogListening(_socketPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting connections, drains workers, and removes the session socket.
    /// </summary>
    /// <param name="cancellationToken">The host shutdown cancellation token.</param>
    /// <returns>A task that completes after the socket is removed.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_shutdownSource is null)
        {
            return;
        }

        await _shutdownSource.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        _connections.Writer.TryComplete();
        if (_acceptTask is not null)
        {
            await _acceptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(_connectionWorkers)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        DeleteSocket();
    }

    /// <summary>
    /// Releases the listener, connection workers, and per-session socket path.
    /// </summary>
    /// <returns>A task that completes after all control resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _listener?.Dispose();
        _shutdownSource?.Dispose();
        DeleteSocket();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket connection = await _listener!
                    .AcceptAsync(cancellationToken)
                    .ConfigureAwait(false);
                await _connections.Writer
                    .WriteAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            LogExpectedShutdown(exception);
        }
        catch (ObjectDisposedException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            LogExpectedShutdown(exception);
        }
        catch (SocketException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            LogExpectedShutdown(exception);
        }
        finally
        {
            _connections.Writer.TryComplete();
        }
    }

    private async Task RunConnectionWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Socket connection in _connections.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RunConnectionAsync(connection, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    LogConnectionEnded(exception.Message);
                }
                catch (IOException exception)
                {
                    LogConnectionEnded(exception.Message);
                }
                catch (ConnectionLostException exception)
                {
                    LogConnectionEnded(exception.Message);
                }
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            LogExpectedShutdown(exception);
        }
        catch (ObjectDisposedException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            LogExpectedShutdown(exception);
        }
    }

    private async Task RunConnectionAsync(
        Socket connection,
        CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(connection, ownsSocket: true);
        using CancellationTokenRegistration shutdownRegistration = cancellationToken.Register(
            static state => ((NetworkStream)state!).Dispose(),
            stream);
        using var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = ControlRpcJson.CreateSerializerOptions()
        };
        using var boundedStream = new BoundedMessageStream(
            stream,
            MaximumMessageBytes,
            leaveOpen: true);
        using var messageHandler = new LengthHeaderMessageHandler(
            boundedStream,
            boundedStream,
            formatter);
        using var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-control-server"
        };
        ControlMethodRegistry.Register(rpc, _target);
        rpc.StartListening();
        await rpc.Completion.ConfigureAwait(false);
        await rpc.DispatchCompletion.ConfigureAwait(false);
    }

    private void DeleteSocket()
    {
        if (_socketPath is not null)
        {
            File.Delete(_socketPath);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Control socket listening at {SocketPath}")]
    private partial void LogListening(string socketPath);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Control connection ended: {Reason}")]
    private partial void LogConnectionEnded(string reason);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Trace,
        Message = "Control socket operation ended during expected shutdown")]
    private partial void LogExpectedShutdown(Exception exception);
}
