using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Owns one protocol-neutral debugger session exposed through private local RPC.
/// </summary>
public sealed partial class DebuggerControlService :
    IDebuggerControlTarget,
    IDebuggerSessionObserver,
    IAsyncDisposable
{
    private readonly Lock _stateLock = new();
    private readonly DebuggerSession _session;
    private DebugSessionSnapshot _snapshot = new() { State = DebugSessionState.Created };

    /// <summary>
    /// Creates one empty debugger control session.
    /// </summary>
    public DebuggerControlService()
    {
        _session = DebuggerEngine.CreateSession(this);
    }

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> GetSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetSnapshot());
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> LaunchAsync(
        DebugLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.LaunchManagedAsync(
            new DebuggeeLaunchOptions
            {
                Program = request.Program,
                WorkingDirectory = request.WorkingDirectory,
                Arguments = request.Arguments,
                Environment = request.Environment,
                RuntimeHostPath = request.RuntimeHostPath,
                SourceFileMap = request.SourceFileMap,
                SourceLinkOptions = request.SourceLinkOptions
            },
            cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.ConfigureSourceOptionsAsync(
            request.SourceFileMap,
            request.SourceLinkOptions,
            cancellationToken).ConfigureAwait(false);
        await _session.AttachManagedAsync(request.ProcessId, cancellationToken)
            .ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        DebugSourceBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.SetSourceBreakpointsAsync(
            request.SourcePath,
            request.Breakpoints,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> PauseAsync(CancellationToken cancellationToken)
    {
        await _session.PauseAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> ContinueAsync(CancellationToken cancellationToken)
    {
        await _session.ContinueAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> StepAsync(
        DebugStepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.StepAsync(request.ThreadId, request.Kind, cancellationToken)
            .ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken) => _session.GetThreadsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DebugStackTrace> GetStackAsync(
        DebugStackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetStackTraceAsync(
            request.ThreadId,
            request.StartFrame,
            request.Levels,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        DebugScopesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetScopesAsync(request.FrameId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        DebugVariablesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetVariablesAsync(
            request.VariablesReference,
            request.Start,
            request.Count,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> TerminateAsync(CancellationToken cancellationToken)
    {
        await _session.TerminateAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> DetachAsync(CancellationToken cancellationToken)
    {
        await _session.DetachAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private DebugSessionSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return _snapshot;
        }
    }

    private void UpdateSnapshot(DebugSessionSnapshot snapshot)
    {
        lock (_stateLock)
        {
            _snapshot = snapshot;
        }
    }
}
