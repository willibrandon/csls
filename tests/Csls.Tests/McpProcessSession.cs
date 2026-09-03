using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Hosts an official MCP client over the redirected streams of a real production launcher.
/// </summary>
internal sealed class McpProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardErrorTask;
    private int _disconnectState;
    private int _disposeState;

    private McpProcessSession(
        Process process,
        Task<string> standardErrorTask,
        McpClient client)
    {
        _process = process;
        _standardErrorTask = standardErrorTask;
        Client = client;
    }

    /// <summary>
    /// Gets the official MCP client connected to the production launcher streams.
    /// </summary>
    internal McpClient Client { get; }

    /// <summary>
    /// Starts the production MCP launcher and connects the official stream transport.
    /// </summary>
    /// <param name="repositoryRoot">The repository working directory.</param>
    /// <param name="mcpPath">The managed or native MCP launcher path.</param>
    /// <param name="mcpWorkerPath">The managed MCP worker path.</param>
    /// <param name="serverWorkerPath">The optional language-server worker path.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <param name="debuggerWorkerPath">The optional debugger worker path.</param>
    /// <returns>The connected real-process MCP session.</returns>
    internal static async Task<McpProcessSession> StartAsync(
        string repositoryRoot,
        string mcpPath,
        string mcpWorkerPath,
        string? serverWorkerPath,
        CancellationToken cancellationToken,
        string? debuggerWorkerPath = null)
    {
        string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
        environment["DOTNET_HOST_PATH"] = dotnetHost;
        environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        if (serverWorkerPath is not null)
        {
            environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
        }

        if (debuggerWorkerPath is not null)
        {
            environment["CSLS_DEBUGGER_WORKER_PATH"] = debuggerWorkerPath;
        }

        bool isManagedLauncher = string.Equals(
            Path.GetExtension(mcpPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedLauncher ? dotnetHost : mcpPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        if (isManagedLauncher)
        {
            startInfo.ArgumentList.Add(mcpPath);
        }

        startInfo.Environment.Clear();
        foreach ((string name, string? value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The production MCP launcher did not start.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            CancellationToken.None);
        var transport = new StreamClientTransport(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream);
        try
        {
            McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new McpProcessSession(process, standardErrorTask, client);
        }
        catch
        {
            await DisposeFailedStartAsync(process, standardErrorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeFailedStartAsync(
        Process process,
        Task<string> standardErrorTask)
    {
        await process.StandardInput.DisposeAsync().ConfigureAwait(false);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ValueTask<string> standardErrorCompletion = new(standardErrorTask);
        await standardErrorCompletion.ConfigureAwait(false);
        process.Dispose();
    }

    /// <summary>
    /// Closes the real protocol input and waits for the production launcher to stop cleanly.
    /// </summary>
    /// <param name="timeout">The maximum clean-disconnect interval.</param>
    /// <param name="cancellationToken">The wait cancellation token.</param>
    /// <returns>The launcher's complete standard-error diagnostics.</returns>
    internal async Task<string> DisconnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disconnectState, 1) != 0)
        {
            throw new InvalidOperationException("The MCP process session is already disconnected.");
        }

        await _process.StandardInput.DisposeAsync().ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);
        ValueTask<string> standardErrorCompletion = new(_standardErrorTask);
        string diagnostics = await standardErrorCompletion.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The MCP launcher exited with code {_process.ExitCode}.{Environment.NewLine}" +
                diagnostics);
        }

        return diagnostics;
    }

    /// <summary>
    /// Releases the MCP streams and terminates the real process tree only after clean shutdown fails.
    /// </summary>
    /// <returns>A task that completes after all test process resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Task disconnectCompletion = Volatile.Read(ref _disconnectState) == 0
            ? DisconnectAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
            : Task.CompletedTask;
        await disconnectCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        ValueTask<string> standardErrorCompletion = new(_standardErrorTask);
        await standardErrorCompletion.ConfigureAwait(false);
        _process.Dispose();
        if (disconnectCompletion.IsFaulted)
        {
            await disconnectCompletion.ConfigureAwait(false);
        }
    }
}
