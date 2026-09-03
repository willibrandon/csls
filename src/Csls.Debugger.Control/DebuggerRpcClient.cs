using Csls.Control;
using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes one explicitly selected private debugger control session.
/// </summary>
public sealed partial class DebuggerRpcClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _socketPath;
    private Socket? _socket;
    private NetworkStream? _stream;
    private BoundedMessageStream? _boundedStream;
    private LengthHeaderMessageHandler? _handler;
    private SystemTextJsonFormatter? _formatter;
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
            throw new ArgumentException("The debugger socket path must be absolute.", nameof(socketPath));
        }

        _socketPath = Path.GetFullPath(socketPath);
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

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await _socket.ConnectAsync(
            new UnixDomainSocketEndPoint(_socketPath),
            cancellationToken).ConfigureAwait(false);
        _stream = new NetworkStream(_socket, ownsSocket: false);
        _boundedStream = new BoundedMessageStream(
            _stream,
            MaximumMessageBytes,
            leaveOpen: true);
        _formatter = DebuggerControlJson.CreateFormatter();
        _handler = new LengthHeaderMessageHandler(_boundedStream, _boundedStream, _formatter);
        _rpc = new JsonRpc(_handler);
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
    /// Gets the current target session snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The current snapshot.</returns>
    public Task<DebugSessionSnapshot> GetSessionAsync(CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugSessionSnapshot>(
            DebuggerControlMethods.GetSession,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Launches a managed target.
    /// </summary>
    /// <param name="request">The concrete target launch.</param>
    /// <param name="cancellationToken">Cancels runtime activation.</param>
    /// <returns>The post-launch snapshot.</returns>
    public Task<DebugSessionSnapshot> LaunchAsync(
        DebugLaunchRequest request,
        CancellationToken cancellationToken) => InvokeAsync<DebugLaunchRequest, DebugSessionSnapshot>(
            DebuggerControlMethods.Launch,
            request,
            cancellationToken);

    /// <summary>
    /// Attaches to a running CoreCLR process.
    /// </summary>
    /// <param name="request">The selected process.</param>
    /// <param name="cancellationToken">Cancels runtime attachment.</param>
    /// <returns>The post-attachment snapshot.</returns>
    public Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken) => InvokeAsync<DebugAttachRequest, DebugSessionSnapshot>(
            DebuggerControlMethods.Attach,
            request,
            cancellationToken);

    /// <summary>
    /// Replaces source breakpoints for one document.
    /// </summary>
    /// <param name="request">The complete replacement breakpoint set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The ordered current breakpoint states.</returns>
    public Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        DebugSourceBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSourceBreakpointSetRequest, IReadOnlyList<DebugSourceBreakpointInfo>>(
            DebuggerControlMethods.SetSourceBreakpoints,
            request,
            cancellationToken);

    /// <summary>
    /// Pauses the managed target.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The stopped snapshot.</returns>
    public Task<DebugSessionSnapshot> PauseAsync(CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugSessionSnapshot>(
            DebuggerControlMethods.Pause,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Continues the managed target.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The running snapshot.</returns>
    public Task<DebugSessionSnapshot> ContinueAsync(CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugSessionSnapshot>(
            DebuggerControlMethods.Continue,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Steps one managed thread.
    /// </summary>
    /// <param name="request">The selected thread and step kind.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The running snapshot.</returns>
    public Task<DebugSessionSnapshot> StepAsync(
        DebugStepRequest request,
        CancellationToken cancellationToken) => InvokeAsync<DebugStepRequest, DebugSessionSnapshot>(
            DebuggerControlMethods.Step,
            request,
            cancellationToken);

    /// <summary>
    /// Gets current managed threads.
    /// </summary>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The current threads.</returns>
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<IReadOnlyList<DebugThreadInfo>>(
            DebuggerControlMethods.GetThreads,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Gets a managed stack page.
    /// </summary>
    /// <param name="request">The selected thread and page.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The requested stack page.</returns>
    public Task<DebugStackTrace> GetStackAsync(
        DebugStackRequest request,
        CancellationToken cancellationToken) => InvokeAsync<DebugStackRequest, DebugStackTrace>(
            DebuggerControlMethods.GetStack,
            request,
            cancellationToken);

    /// <summary>
    /// Gets frame scopes.
    /// </summary>
    /// <param name="request">The selected frame.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The frame scopes.</returns>
    public Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        DebugScopesRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugScopesRequest, IReadOnlyList<DebugScopeInfo>>(
            DebuggerControlMethods.GetScopes,
            request,
            cancellationToken);

    /// <summary>
    /// Gets a variable page.
    /// </summary>
    /// <param name="request">The selected container and page.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The requested variable page.</returns>
    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        DebugVariablesRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugVariablesRequest, IReadOnlyList<DebugVariableInfo>>(
            DebuggerControlMethods.GetVariables,
            request,
            cancellationToken);

    /// <summary>
    /// Terminates the debugger-owned target.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for termination.</param>
    /// <returns>The terminal snapshot.</returns>
    public Task<DebugSessionSnapshot> TerminateAsync(CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugSessionSnapshot>(
            DebuggerControlMethods.Terminate,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Detaches from the target without terminating it.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for detachment.</param>
    /// <returns>The terminal snapshot.</returns>
    public Task<DebugSessionSnapshot> DetachAsync(CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugSessionSnapshot>(
            DebuggerControlMethods.Detach,
            cancellationToken: cancellationToken);

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
        if (_boundedStream is not null)
        {
            await _boundedStream.DisposeAsync().ConfigureAwait(false);
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _socket?.Dispose();
    }

    private JsonRpc GetRpc()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _rpc ?? throw new InvalidOperationException(
            "The debugger RPC client is not connected.");
    }

    private Task<TResult> InvokeAsync<TRequest, TResult>(
        string method,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetRpc().InvokeWithParameterObjectAsync<TResult>(
            method,
            NamedArgs.Create(request),
            cancellationToken);
    }
}
