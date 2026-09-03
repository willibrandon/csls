using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;
using System.Diagnostics;

namespace Csls.Mcp.Worker;

/// <summary>
/// Serializes operations and owns one supervised debugger worker process.
/// </summary>
internal sealed partial class McpDebuggerSession : IAsyncDisposable
{
    private readonly Process _worker;
    private readonly ValueTask<string> _diagnostics;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposeState;

    /// <summary>
    /// Creates a connected session whose ownership is initially held by a lease.
    /// </summary>
    /// <param name="id">The stable MCP session identifier.</param>
    /// <param name="kind">How the target will be acquired.</param>
    /// <param name="worker">The supervised debugger worker.</param>
    /// <param name="diagnostics">The bounded worker diagnostics reader.</param>
    /// <param name="client">The private debugger RPC client.</param>
    internal McpDebuggerSession(
        string id,
        McpDebuggerSessionKind kind,
        Process worker,
        ValueTask<string> diagnostics,
        DebuggerRpcClient client)
    {
        Id = id;
        Kind = kind;
        _worker = worker;
        _diagnostics = diagnostics;
        _agentControlExpirationTimer = new Timer(
            static state => ((McpDebuggerSession)state!).ExpireAgentControl(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        Client = client;
        Client.ResourceChanged += OnResourceChanged;
    }

    /// <summary>
    /// Gets the stable opaque MCP debugger-session identifier.
    /// </summary>
    internal string Id { get; }

    /// <summary>
    /// Gets how the debugger acquired its target.
    /// </summary>
    internal McpDebuggerSessionKind Kind { get; }

    /// <summary>
    /// Gets the private RPC client connected to the debugger worker.
    /// </summary>
    internal DebuggerRpcClient Client { get; }

    /// <summary>
    /// Signals authoritative resource changes from the debugger worker.
    /// </summary>
    internal event Action<McpDebuggerResourceChange>? ResourceChanged;

    private void OnResourceChanged(object? sender, DebuggerResourceChangeEventArgs change)
    {
        _ = sender;
        ResourceChanged?.Invoke(new McpDebuggerResourceChange(Id, change.Kind));
    }

    /// <summary>
    /// Invokes one serialized operation against the debugger worker.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The private debugger operation.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The operation result.</returns>
    internal async Task<T> InvokeAsync<T>(
        Func<DebuggerRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            return await operation(Client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            throw new McpDebuggerException(
                "debugger_connection_lost",
                $"Debugger session {Id} disconnected: {exception.Message}");
        }
        catch (RemoteInvocationException exception)
        {
            throw new McpDebuggerException(
                "debugger_operation_failed",
                exception.Message);
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    /// <summary>
    /// Gets the current public session projection.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The current session projection.</returns>
    internal async Task<McpDebugSessionInfo> GetInfoAsync(
        CancellationToken cancellationToken)
    {
        DebugSessionSnapshot snapshot = await InvokeAsync(
            static (client, token) => client.GetSessionAsync(token),
            cancellationToken).ConfigureAwait(false);
        return CreateInfo(snapshot);
    }

    /// <summary>
    /// Ends the target using safe ownership defaults.
    /// </summary>
    /// <param name="terminateAttachedTarget">Whether an attached target is explicitly terminated.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The terminal session projection.</returns>
    internal async Task<McpDebugSessionInfo> EndAsync(
        bool terminateAttachedTarget,
        CancellationToken cancellationToken)
    {
        if (terminateAttachedTarget && Kind == McpDebuggerSessionKind.Attach && !AgentControl)
        {
            throw new McpDebuggerException(
                "debugger_control_denied",
                $"Debugger session {Id} has no active agent-control grant.");
        }

        DebugSessionSnapshot current = await InvokeAsync(
            static (client, token) => client.GetSessionAsync(token),
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot ended = current.State is
            DebugSessionState.Terminated or DebugSessionState.Faulted
            ? current
            : Kind == McpDebuggerSessionKind.Launch
                ? await InvokeAsync(
                    static (client, token) => client.TerminateAsync(token),
                    cancellationToken).ConfigureAwait(false)
                : terminateAttachedTarget
                    ? await InvokeControlledAsync(
                        static (client, token) => client.TerminateAsync(token),
                        cancellationToken).ConfigureAwait(false)
                : await InvokeAsync(
                    static (client, token) => client.DetachAsync(token),
                    cancellationToken).ConfigureAwait(false);
        return CreateInfo(ended);
    }
}
