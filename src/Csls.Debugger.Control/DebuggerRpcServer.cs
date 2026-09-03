using Csls.Debugger.Contracts;
using System.Net.Sockets;

namespace Csls.Debugger.Control;

/// <summary>
/// Hosts one bounded private debugger control connection on a Unix-domain socket.
/// </summary>
public sealed class DebuggerRpcServer : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly IDebuggerControlTarget _target;
    private readonly CancellationTokenSource _lifetime = new();
    private Socket? _listener;
    private Task? _runTask;
    private bool _bound;
    private int _disposed;

    /// <summary>
    /// Creates a server for an explicit absolute socket path and debugger target.
    /// </summary>
    /// <param name="socketPath">The absolute private socket path.</param>
    /// <param name="target">The debugger control implementation.</param>
    public DebuggerRpcServer(string socketPath, IDebuggerControlTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentNullException.ThrowIfNull(target);
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException("The debugger socket path must be absolute.", nameof(socketPath));
        }

        _socketPath = Path.GetFullPath(socketPath);
        _target = target;
    }

    /// <summary>
    /// Binds the private socket and begins accepting the single owning client.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The debugger RPC server has already started.");
        }

        string directory = Path.GetDirectoryName(_socketPath)
            ?? throw new InvalidOperationException("The debugger socket has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_socketPath))
        {
            throw new IOException($"The debugger socket path already exists: {_socketPath}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);
        _bound = true;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _runTask = RunAsync(_lifetime.Token);
    }

    /// <summary>
    /// Waits for the owning connection to finish.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this wait.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    public async Task WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        Task runTask = _runTask
            ?? throw new InvalidOperationException("The debugger RPC server has not started.");
        await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                System.Diagnostics.Debug.Assert(_lifetime.IsCancellationRequested);
            }
        }

        _lifetime.Dispose();
        if (_bound)
        {
            File.Delete(_socketPath);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using Socket connection = await _listener!.AcceptAsync(cancellationToken)
            .ConfigureAwait(false);
        using var stream = new NetworkStream(connection, ownsSocket: false);
        await DebuggerRpcStreamServer.RunAsync(
            stream,
            stream,
            _target,
            cancellationToken).ConfigureAwait(false);
    }
}
