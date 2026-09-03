using Csls.Control;
using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;

namespace Csls.Debugger.Control;

/// <summary>
/// Connects and releases debugger RPC transports.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string? _socketPath;
    private readonly bool _leaveStreamsOpen;
    private Socket? _socket;
    private Stream? _sendingStream;
    private Stream? _receivingStream;
    private BoundedMessageStream? _boundedSendingStream;
    private BoundedMessageStream? _boundedReceivingStream;
    private LengthHeaderMessageHandler? _handler;
    private NerdbankMessagePackFormatter? _formatter;
    private JsonRpc? _rpc;
    private int _disposed;

    /// <summary>
    /// Creates a client for an explicit absolute debugger socket path.
    /// </summary>
    /// <param name="socketPath">The absolute private socket path.</param>
    public DebuggerRpcClient(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException(
                "The debugger socket path must be absolute.",
                nameof(socketPath));
        }

        _socketPath = Path.GetFullPath(socketPath);
    }

    /// <summary>
    /// Creates a client over caller-selected private sending and receiving streams.
    /// </summary>
    /// <param name="sendingStream">The writable stream carrying requests.</param>
    /// <param name="receivingStream">The readable stream carrying responses.</param>
    /// <param name="leaveOpen">Whether disposal leaves both streams open.</param>
    public DebuggerRpcClient(
        Stream sendingStream,
        Stream receivingStream,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(sendingStream);
        ArgumentNullException.ThrowIfNull(receivingStream);
        if (!sendingStream.CanWrite)
        {
            throw new ArgumentException("The sending stream must be writable.", nameof(sendingStream));
        }

        if (!receivingStream.CanRead)
        {
            throw new ArgumentException("The receiving stream must be readable.", nameof(receivingStream));
        }

        _sendingStream = sendingStream;
        _receivingStream = receivingStream;
        _leaveStreamsOpen = leaveOpen;
    }

    /// <summary>
    /// Connects to the selected debugger session and verifies its protocol version.
    /// </summary>
    /// <param name="cancellationToken">Cancels connection establishment.</param>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_rpc is not null)
        {
            throw new InvalidOperationException("The debugger RPC client is already connected.");
        }

        if (_socketPath is not null)
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(
                new UnixDomainSocketEndPoint(_socketPath),
                cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(_socket, ownsSocket: false);
            _sendingStream = stream;
            _receivingStream = stream;
        }

        Stream sendingStream = _sendingStream
            ?? throw new InvalidOperationException("The debugger RPC sending stream is unavailable.");
        Stream receivingStream = _receivingStream
            ?? throw new InvalidOperationException("The debugger RPC receiving stream is unavailable.");
        _boundedSendingStream = new BoundedMessageStream(
            sendingStream,
            MaximumMessageBytes,
            leaveOpen: true);
        _boundedReceivingStream = ReferenceEquals(sendingStream, receivingStream)
            ? _boundedSendingStream
            : new BoundedMessageStream(receivingStream, MaximumMessageBytes, leaveOpen: true);
        _formatter = DebuggerControlRpcFormatter.Create();
        _handler = new LengthHeaderMessageHandler(
            _boundedSendingStream,
            _boundedReceivingStream,
            _formatter);
        _rpc = new JsonRpc(_handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "debugger-control-client"
        };
        _rpc.AddLocalRpcMethod(
            DebuggerControlNotifications.ResourceChanged,
            new Action<DebuggerResourceChangeEventArgs>(OnResourceChanged));
        _rpc.StartListening();
        int version = await _rpc.InvokeWithCancellationAsync<int>(
            DebuggerControlMethods.GetProtocolVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (version != DebuggerControlProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Debugger control protocol {version} is incompatible with " +
                $"{DebuggerControlProtocol.CurrentVersion}.");
        }
    }

    /// <summary>
    /// Signals that authoritative state or output changed in the debugger worker.
    /// </summary>
    public event EventHandler<DebuggerResourceChangeEventArgs>? ResourceChanged;

    private void OnResourceChanged(DebuggerResourceChangeEventArgs change) =>
        ResourceChanged?.Invoke(this, change);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _rpc?.Dispose();
        if (_handler is not null)
        {
            await _handler.DisposeAsync().ConfigureAwait(false);
        }

        _formatter?.Dispose();
        await DisposeDistinctAsync(
            _boundedSendingStream,
            _boundedReceivingStream).ConfigureAwait(false);
        if (!_leaveStreamsOpen)
        {
            await DisposeDistinctAsync(_sendingStream, _receivingStream).ConfigureAwait(false);
        }

        _socket?.Dispose();
    }

    private static async Task DisposeDistinctAsync(
        IAsyncDisposable? first,
        IAsyncDisposable? second)
    {
        if (first is not null)
        {
            await first.DisposeAsync().ConfigureAwait(false);
        }

        if (second is not null && !ReferenceEquals(first, second))
        {
            await second.DisposeAsync().ConfigureAwait(false);
        }
    }
}
