using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Observes a launcher-owned target whose standard handles belong to a client terminal.
/// </summary>
internal sealed class DebuggeeTerminalProcess : IDebuggeeProcess
{
    private readonly Process? _process;
    private readonly int _id;
    private readonly string _name;
    private readonly bool _terminateChildProcesses;
    private readonly Func<CancellationToken, Task<int>> _readExitCode;
    private readonly Lock _exitGate = new();
    private Task<int>? _exitCode;
    private int _detached;
    private int _disposed;

    private DebuggeeTerminalProcess(
        Process? process,
        int id,
        string name,
        bool terminateChildProcesses,
        Func<CancellationToken, Task<int>> readExitCode)
    {
        _process = process;
        _id = id;
        _name = name;
        _terminateChildProcesses = terminateChildProcesses;
        _readExitCode = readExitCode;
    }

    /// <inheritdoc />
    public int Id => _id;

    /// <inheritdoc />
    public string Name => _name;

    /// <inheritdoc />
    public bool OwnsProcess => true;

    /// <summary>
    /// Retains a launched target, including one that exited before its PID was received.
    /// </summary>
    /// <param name="options">The validated target launch policy.</param>
    /// <param name="processId">The authenticated launcher child identifier.</param>
    /// <param name="readExitCode">Reads the exact direct-parent exit report.</param>
    /// <returns>The owned terminal target.</returns>
    internal static DebuggeeTerminalProcess Open(
        DebuggeeLaunchOptions options,
        int processId,
        Func<CancellationToken, Task<int>> readExitCode)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readExitCode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        Process? process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            process = null;
        }

        return new DebuggeeTerminalProcess(
            process,
            processId,
            Path.GetFileNameWithoutExtension(options.Program),
            options.TerminateChildProcesses,
            readExitCode);
    }

    /// <inheritdoc />
    public Task CopyStandardOutputAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CopyStandardErrorAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        GetExitCodeAsync().WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        KillOwnedProcess();
        _ = await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Detach() => Volatile.Write(ref _detached, 1);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_process is not null)
        {
            using (_process)
            {
                if (Volatile.Read(ref _detached) == 0)
                {
                    KillOwnedProcess();
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private void KillOwnedProcess()
    {
        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: _terminateChildProcesses);
        }
    }

    private Task<int> GetExitCodeAsync()
    {
        lock (_exitGate)
        {
            return _exitCode ??= _readExitCode(CancellationToken.None);
        }
    }
}
