using Csls.Control.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;

namespace Csls.Control;

/// <summary>
/// Discovers bounded live-session state through the private per-user socket directory.
/// </summary>
public static class ControlSessionDiscovery
{
    private const int MaximumSessionSockets = 256;
    private static readonly TimeSpan s_connectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns every responsive session in deterministic process-identifier order.
    /// </summary>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>The responsive live-session snapshots.</returns>
    public static async Task<IReadOnlyList<ControlSessionInfo>> DiscoverAsync(
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
            ControlSessionInfo? session = await TryGetSessionAsync(
                socketPath,
                cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        sessions.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return sessions;
    }

    private static async Task<ControlSessionInfo?> TryGetSessionAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(s_connectionTimeout);
        var client = new ControlRpcClient(socketPath);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return await client
                    .GetSessionAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or TimeoutException or
                    ConnectionLostException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
