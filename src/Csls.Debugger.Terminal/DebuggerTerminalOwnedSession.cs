using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Owns one terminal debugger's target, private transport, and rendered state.
/// </summary>
internal sealed class DebuggerTerminalOwnedSession : IAsyncDisposable
{
    private readonly DebuggerWorkerProcess _worker;
    private DebuggerTerminalState? _state;
    private int _disposed;

    private DebuggerTerminalOwnedSession(
        DebuggerWorkerProcess worker,
        string displayName)
    {
        _worker = worker;
        DisplayName = displayName;
    }

    /// <summary>
    /// Gets the terminal-local identity used to reject stale view callbacks.
    /// </summary>
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets the human-readable target selection label.
    /// </summary>
    internal string DisplayName { get; }

    /// <summary>
    /// Gets the active terminal state after target activation completes.
    /// </summary>
    internal DebuggerTerminalState State => _state ?? throw new InvalidOperationException(
        "The terminal debugger session has not activated.");

    /// <summary>
    /// Creates and activates one terminal-owned launch session.
    /// </summary>
    internal static Task<DebuggerTerminalOwnedSession> LaunchAsync(
        DebuggerTerminalLaunchOptions options,
        CancellationToken cancellationToken) => CreateAsync(
        async (client, token) =>
        {
            if (options.SourcePath is string sourcePath && options.Line is int line)
            {
                _ = await client.SetSourceBreakpointsAsync(
                    new DebugSourceBreakpointSetRequest(
                        sourcePath,
                        [new DebugSourceBreakpointRequest(line, null)]),
                    token).ConfigureAwait(false);
            }

            _ = await client.LaunchAsync(
                new DebugLaunchRequest
                {
                    Program = options.Program,
                    WorkingDirectory = options.WorkingDirectory,
                    Arguments = options.Arguments,
                    EnvironmentFilePath = options.EnvironmentFilePath,
                    RuntimeHostPath = options.RuntimeHostPath,
                    SourceFileMap = options.SourceFileMap,
                    RequireExactSource = options.RequireExactSource,
                    ExpressionEvaluationOptions = options.ExpressionEvaluationOptions,
                    StopAtEntry = options.StopAtEntry,
                    TerminateChildProcesses = options.TerminateChildProcesses
                },
                token).ConfigureAwait(false);
        }, Path.GetFileName(options.Program), cancellationToken);

    /// <summary>
    /// Creates and pauses one terminal-owned attached session.
    /// </summary>
    internal static Task<DebuggerTerminalOwnedSession> AttachAsync(
        DebuggerTerminalAttachOptions options,
        CancellationToken cancellationToken) => CreateAsync(
        async (client, token) =>
        {
            _ = await client.AttachAsync(
                new DebugAttachRequest(options.ProcessId)
                {
                    RequireExactSource = options.RequireExactSource,
                    ExpressionEvaluationOptions = options.ExpressionEvaluationOptions,
                    SourceFileMap = options.SourceFileMap
                },
                token).ConfigureAwait(false);
            _ = await client.PauseAsync(token).ConfigureAwait(false);
        }, $"PID {options.ProcessId}", cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await using ConfiguredAsyncDisposable workerCleanup = _worker.ConfigureAwait(false);
        if (_state is not null)
        {
            await _state.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DebuggerTerminalOwnedSession> CreateAsync(
        Func<DebuggerRpcClient, CancellationToken, Task> activate,
        string displayName,
        CancellationToken cancellationToken)
    {
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(
            DebuggerTerminalWorkerLocator.ResolveCurrent(),
            configureNativeEnvironment: true,
            cancellationToken).ConfigureAwait(false);
        var owned = new DebuggerTerminalOwnedSession(worker, displayName);
        try
        {
            await activate(worker.Client, cancellationToken).ConfigureAwait(false);
            owned._state = await DebuggerTerminalState.CreateAsync(worker.Client, cancellationToken)
                .ConfigureAwait(false);
            return owned;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
