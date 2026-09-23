using Csls.Debugger.Control;

namespace Csls.Mcp.Worker;

/// <summary>
/// Starts and releases the supervised debugger worker process.
/// </summary>
internal sealed partial class McpDebuggerSession
{
    /// <summary>
    /// Starts one debugger worker connected only through inherited standard-stream handles.
    /// </summary>
    /// <param name="workerPath">The absolute debugger worker path.</param>
    /// <param name="id">The stable MCP session identifier.</param>
    /// <param name="kind">How the target will be acquired.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>An ownership lease for the connected supervised session.</returns>
    internal static async Task<McpDebuggerSessionLease> StartAsync(
        string workerPath,
        string id,
        McpDebuggerSessionKind kind,
        CancellationToken cancellationToken)
    {
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(
            workerPath,
            configureNativeEnvironment: kind is not McpDebuggerSessionKind.Dump,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return McpDebuggerSessionLease.Create(id, kind, worker);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        lock (_agentControlGate)
        {
            ClearAgentControl();
            _agentControlExpirationTimer.Dispose();
        }

        using (_operationGate)
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Client.ResourceChanged -= OnResourceChanged;
                await _worker.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _ = _operationGate.Release();
            }
        }
    }
}
