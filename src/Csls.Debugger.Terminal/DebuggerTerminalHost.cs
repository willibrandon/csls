using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Hex1b;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Runs interactive Hex1b clients against private debugger control sessions.
/// </summary>
public static class DebuggerTerminalHost
{
    /// <summary>
    /// Launches a managed target and opens the debugger at its initial source breakpoint.
    /// </summary>
    /// <param name="options">The validated launch and breakpoint options.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The debugger process exit code.</returns>
    public static async Task<int> RunLaunchAsync(
        DebuggerTerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateLaunch(options);
        using var endpoint = DebuggerTerminalEndpoint.Create();
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        var server = new DebuggerRpcServer(endpoint.SocketPath, service);
        await using ConfiguredAsyncDisposable serverCleanup = server.ConfigureAwait(false);
        server.Start();
        var client = new DebuggerRpcClient(endpoint.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                options.SourcePath,
                [new DebugSourceBreakpointRequest(options.Line, null)]),
            cancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = options.Program,
                WorkingDirectory = options.WorkingDirectory,
                Arguments = options.Arguments,
                RuntimeHostPath = options.RuntimeHostPath,
                SourceFileMap = options.SourceFileMap
            },
            cancellationToken).ConfigureAwait(false);
        return await RunTerminalAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches to and pauses a running managed target before opening the debugger.
    /// </summary>
    /// <param name="options">The validated target process selection.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The debugger process exit code.</returns>
    public static async Task<int> RunAttachAsync(
        DebuggerTerminalAttachOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProcessId);
        using var endpoint = DebuggerTerminalEndpoint.Create();
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        var server = new DebuggerRpcServer(endpoint.SocketPath, service);
        await using ConfiguredAsyncDisposable serverCleanup = server.ConfigureAwait(false);
        server.Start();
        var client = new DebuggerRpcClient(endpoint.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _ = await client.AttachAsync(
            new DebugAttachRequest(options.ProcessId)
            {
                SourceFileMap = options.SourceFileMap
            },
            cancellationToken).ConfigureAwait(false);
        _ = await client.PauseAsync(cancellationToken).ConfigureAwait(false);
        return await RunTerminalAsync(client, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunTerminalAsync(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        DebuggerTerminalState state = await DebuggerTerminalState
            .CreateAsync(client, cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable stateCleanup = state.ConfigureAwait(false);
        Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(
                static _ => { },
                app =>
                {
                    state.AttachApp(app);
                    return context => DebuggerTerminalView.Build(context, state);
                })
            .WithMouse()
            .Build();
        await using (terminal.ConfigureAwait(false))
        {
            await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static void ValidateLaunch(DebuggerTerminalLaunchOptions options)
    {
        if (!Path.IsPathFullyQualified(options.Program) || !File.Exists(options.Program))
        {
            throw new FileNotFoundException("The managed program does not exist.", options.Program);
        }

        if (!Path.IsPathFullyQualified(options.WorkingDirectory) ||
            !Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The target working directory does not exist: {options.WorkingDirectory}");
        }

        if (!Path.IsPathFullyQualified(options.SourcePath) || !File.Exists(options.SourcePath))
        {
            throw new FileNotFoundException(
                "The initial breakpoint source does not exist.",
                options.SourcePath);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Line);
    }
}
