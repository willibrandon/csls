using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using StreamJsonRpc;

namespace Csls.DebugAdapter;

/// <summary>
/// Owns a managed dump worker and projects its read-only inspection operations.
/// </summary>
internal sealed class DapDumpSession : IDebuggerInspectionTarget, IAsyncDisposable
{
    /// <summary>
    /// Describes an inspection interrupted by loss of its owned worker.
    /// </summary>
    internal const string DisconnectedMessage = "The managed dump worker disconnected during inspection.";

    private readonly DebuggerWorkerProcess _worker;
    private readonly Task _workerExit;

    private DapDumpSession(DebuggerWorkerProcess worker, DebugSessionSnapshot snapshot)
    {
        _worker = worker;
        Snapshot = snapshot;
        _workerExit = worker.WaitForExitAsync(CancellationToken.None);
        Completion = Task.WhenAny(_workerExit, worker.Client.Disconnected);
    }

    /// <summary>
    /// Gets the immutable snapshot returned by dump activation.
    /// </summary>
    internal DebugSessionSnapshot Snapshot { get; }

    /// <summary>
    /// Gets the observation of the owned worker's process exit or RPC disconnection.
    /// </summary>
    internal Task Completion { get; }

    /// <summary>
    /// Gets whether the worker or its transport has ended independently of asynchronous completion continuations.
    /// </summary>
    internal bool HasDisconnected => _workerExit.IsCompleted || _worker.Client.Disconnected.IsCompleted;

    /// <summary>
    /// Opens a dump through a private supervised worker.
    /// </summary>
    /// <param name="request">The selected dump and managed runtime.</param>
    /// <param name="writeErrorAsync">Receives cleanup diagnostics independently of DAP output.</param>
    /// <param name="cancellationToken">Cancels dump activation.</param>
    /// <returns>The activated dump session and its worker owner.</returns>
    internal static async Task<DapDumpSession> OpenAsync(DebugDumpOpenRequest request,
        Func<string, Task> writeErrorAsync, CancellationToken cancellationToken)
    {
        string workerPath = DebuggerDumpWorkerLocator.TryResolve()
            ?? throw new FileNotFoundException("The debugger installation is missing its managed dump worker.");
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(
            workerPath, configureNativeEnvironment: false, cancellationToken).ConfigureAwait(false);
        try
        {
            DebugSessionSnapshot snapshot = await InvokeAsync(
                () => worker.Client.OpenDumpAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new DapDumpSession(worker, snapshot);
        }
        catch
        {
            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                await writeErrorAsync(exception.Message).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => _worker.Client.GetThreadsAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<DebugStackTrace> GetStackAsync(DebugStackRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(() => _worker.Client.GetStackAsync(request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(DebugScopesRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(() => _worker.Client.GetScopesAsync(request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(DebugVariablesRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(() => _worker.Client.GetVariablesAsync(request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<DebugModulePage> GetModulesAsync(DebugModulesRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(() => _worker.Client.GetModulesAsync(request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _worker.DisposeAsync();

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> invoke, CancellationToken cancellationToken)
    {
        try
        {
            return await invoke().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteInvocationException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (ConnectionLostException exception)
        {
            throw new InvalidOperationException(DisconnectedMessage, exception);
        }
    }
}
