using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Owns one terminal debugger's target, private transport, and rendered state.
/// </summary>
internal sealed class DebuggerTerminalOwnedSession : IAsyncDisposable
{
    private readonly DebuggerTerminalEndpoint _endpoint;
    private readonly DebuggerControlService _service;
    private readonly DebuggerRpcServer _server;
    private readonly DebuggerRpcClient _client;
    private DebuggerTerminalState? _state;
    private int _disposed;

    private DebuggerTerminalOwnedSession(
        DebuggerTerminalEndpoint endpoint,
        DebuggerControlService service,
        DebuggerRpcServer server,
        DebuggerRpcClient client)
    {
        _endpoint = endpoint;
        _service = service;
        _server = server;
        _client = client;
    }

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
        }, cancellationToken);

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
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using DebuggerTerminalEndpoint endpointCleanup = _endpoint;
        await using ConfiguredAsyncDisposable serviceCleanup = _service.ConfigureAwait(false);
        await using ConfiguredAsyncDisposable serverCleanup = _server.ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = _client.ConfigureAwait(false);
        if (_state is not null)
        {
            await _state.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DebuggerTerminalOwnedSession> CreateAsync(
        Func<DebuggerRpcClient, CancellationToken, Task> activate,
        CancellationToken cancellationToken)
    {
        var endpoint = DebuggerTerminalEndpoint.Create();
        DebuggerControlService? service = null;
        DebuggerRpcServer? server = null;
        DebuggerRpcClient? client = null;
        DebuggerTerminalOwnedSession? owned = null;
        try
        {
            service = new DebuggerControlService();
            server = new DebuggerRpcServer(endpoint.SocketPath, service);
            client = new DebuggerRpcClient(endpoint.SocketPath);
            owned = new DebuggerTerminalOwnedSession(endpoint, service, server, client);
            server.Start();
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await activate(client, cancellationToken).ConfigureAwait(false);
            owned._state = await DebuggerTerminalState.CreateAsync(client, cancellationToken)
                .ConfigureAwait(false);
            return owned;
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                if (service is not null)
                {
                    await service.DisposeAsync().ConfigureAwait(false);
                }
                endpoint.Dispose();
            }
            throw;
        }
    }
}
