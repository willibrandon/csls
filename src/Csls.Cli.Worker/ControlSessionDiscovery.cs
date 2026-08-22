using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Cli.Worker;

/// <summary>
/// Discovers bounded live-session state through the private per-user socket directory.
/// </summary>
internal static class ControlSessionDiscovery
{
    private const int MaximumSessionSockets = 256;
    private static readonly TimeSpan s_connectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns every responsive session in deterministic process-identifier order.
    /// </summary>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>The responsive live-session snapshots.</returns>
    internal static async Task<IReadOnlyList<ControlSessionInfo>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        string socketDirectory = ControlEndpoint.GetSocketDirectory();
        if (!Directory.Exists(socketDirectory))
        {
            return [];
        }

        string[] socketPaths =
        [
            .. Directory
            .EnumerateFiles(socketDirectory, "*.csls.socket", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Take(MaximumSessionSockets + 1)
        ];
        if (socketPaths.Length > MaximumSessionSockets)
        {
            throw new InvalidDataException(
                $"Session discovery exceeded {MaximumSessionSockets} socket entries.");
        }

        var sessions = new List<ControlSessionInfo>(socketPaths.Length);
        foreach (string socketPath in socketPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(s_connectionTimeout);
            try
            {
                var client = new ControlRpcClient(socketPath);
                await using ConfiguredAsyncDisposable clientCleanup =
                    client.ConfigureAwait(false);
                ControlSessionInfo session = await client
                    .GetSessionAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
                sessions.Add(session);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or TimeoutException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        sessions.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return sessions;
    }
}
