using Csls.Debugger.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Owns one isolated read-only managed process-dump inspection session.
/// </summary>
public sealed partial class DumpDebuggerControlService :
    IDebuggerControlTarget,
    IAsyncDisposable
{
    private const int MaximumFrames = 4096;
    private const int MaximumModules = 4096;
    private const int MaximumNameCharacters = 4096;
    private const int MaximumThreads = 4096;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<int, IReadOnlyList<DumpStackFrame>> _framesByThread = [];
    private readonly Dictionary<int, DumpStackFrame> _framesById = [];
    private DataTarget? _dataTarget;
    private ClrRuntime? _runtime;
    private IReadOnlyList<DebugModuleInfo> _modules = [];
    private IReadOnlyList<DumpThread> _threads = [];
    private DebugSessionSnapshot _snapshot = new() { State = DebugSessionState.Created };
    private int _nextFrameId = 1;
    private int _disposeState;

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> GetSessionAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => _snapshot, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeTarget();
        }
        finally
        {
            _ = _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    private void RequireOpen()
    {
        if (_runtime is null)
        {
            throw new InvalidOperationException(
                "A managed process dump must be opened before inspection.");
        }
    }

    private static NotSupportedException CreateReadOnlyException(string operation) =>
        new($"Process-dump sessions do not support {operation}.");

    private static string BoundName(string? value, string fallback)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return result.Length <= MaximumNameCharacters
            ? result
            : string.Concat(result.AsSpan(0, MaximumNameCharacters - 1), "…");
    }

    private void DisposeTarget()
    {
        _runtime?.Dispose();
        _runtime = null;
        _dataTarget?.Dispose();
        _dataTarget = null;
        _threads = [];
        _modules = [];
        _framesByThread.Clear();
        _framesById.Clear();
    }
}
