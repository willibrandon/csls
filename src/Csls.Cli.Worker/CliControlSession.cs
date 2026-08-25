using Csls.Client;
using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Cli.Worker;

/// <summary>
/// Owns an attached or transient control client for one CLI operation.
/// </summary>
internal sealed class CliControlSession : IAsyncDisposable
{
    private readonly TransientLanguageServerSession? _transientSession;

    private CliControlSession(
        string socketPath,
        TransientLanguageServerSession? transientSession)
    {
        Client = new ControlRpcClient(socketPath);
        _transientSession = transientSession;
    }

    /// <summary>
    /// Gets the connected production control client.
    /// </summary>
    internal ControlRpcClient Client { get; }

    /// <summary>
    /// Connects to a selected live session or starts a transient workspace session.
    /// </summary>
    /// <param name="processId">The selected process identifier, or zero for discovery or transient mode.</param>
    /// <param name="workspacePath">The optional workspace used for transient mode.</param>
    /// <param name="cancellationToken">The connection cancellation token.</param>
    /// <returns>The connected CLI control session.</returns>
    internal static async Task<CliControlSession> ConnectAsync(
        int processId,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);

        string? fullWorkspacePath = string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : Path.GetFullPath(workspacePath);
        if (processId > 0 && fullWorkspacePath is not null)
        {
            throw new InvalidOperationException(
                "Specify --session or --workspace, but not both.");
        }

        TransientLanguageServerSession? transientSession = null;
        string socketPath;
        try
        {
            if (fullWorkspacePath is not null)
            {
                ControlSessionInfo? existingSession = await ControlSessionDiscovery
                    .TryResolveWorkspaceAsync(fullWorkspacePath, cancellationToken)
                    .ConfigureAwait(false);
                if (existingSession is not null)
                {
                    socketPath = existingSession.SocketPath;
                }
                else
                {
                    transientSession = await TransientLanguageServerSession.StartAsync(
                        fullWorkspacePath,
                        "csls-cli",
                        cancellationToken).ConfigureAwait(false);
                    socketPath = ControlEndpoint.GetSocketPath(transientSession.ProcessId);
                }
            }
            else
            {
                ControlSessionInfo session = await ControlSessionDiscovery.ResolveAsync(
                    processId,
                    workspacePath: null,
                    cancellationToken).ConfigureAwait(false);
                socketPath = session.SocketPath;
            }
        }
        catch
        {
            if (transientSession is not null)
            {
                await transientSession.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        var result = new CliControlSession(socketPath, transientSession);
        try
        {
            await result.Client.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Releases the control connection and any transient language-server process.
    /// </summary>
    /// <returns>A task that completes after all owned processes and transports exit.</returns>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        if (_transientSession is not null)
        {
            await _transientSession.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
