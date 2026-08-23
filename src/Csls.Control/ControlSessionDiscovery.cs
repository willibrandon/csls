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

    /// <summary>
    /// Resolves one responsive session by process identifier, workspace path, or unambiguous discovery.
    /// </summary>
    /// <param name="processId">The requested process identifier, or zero to discover it.</param>
    /// <param name="workspacePath">The optional workspace path used to select or validate a session.</param>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>The single matching responsive session.</returns>
    public static async Task<ControlSessionInfo> ResolveAsync(
        int processId,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        string? fullWorkspacePath = string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : Path.GetFullPath(workspacePath);
        if (processId > 0)
        {
            var client = new ControlRpcClient(ControlEndpoint.GetSocketPath(processId));
            await using (client.ConfigureAwait(false))
            {
                ControlSessionInfo selected = await client.GetSessionAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (selected.ProcessId != processId)
                {
                    throw new InvalidDataException(
                        $"Session socket {processId} identified itself as process {selected.ProcessId}.");
                }

                if (fullWorkspacePath is not null &&
                    !ContainsWorkspace(selected, fullWorkspacePath))
                {
                    throw new InvalidOperationException(
                        $"Session {processId} does not contain workspace {fullWorkspacePath}.");
                }

                return selected;
            }
        }

        IReadOnlyList<ControlSessionInfo> discovered = await DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ControlSessionInfo> matches = fullWorkspacePath is null
            ? discovered
            : [.. discovered.Where(session => ContainsWorkspace(session, fullWorkspacePath))];
        return matches.Count switch
        {
            0 when fullWorkspacePath is null => throw new InvalidOperationException(
                "No live csls session was found. Start an editor session or specify --session."),
            0 => throw new InvalidOperationException(
                $"No live csls session contains workspace {fullWorkspacePath}."),
            1 => matches[0],
            _ when fullWorkspacePath is null => throw new InvalidOperationException(
                "Multiple live csls sessions were found. Specify --session or --workspace."),
            _ => throw new InvalidOperationException(
                $"Multiple live csls sessions contain workspace {fullWorkspacePath}. Specify --session.")
        };
    }

    private static bool ContainsWorkspace(ControlSessionInfo session, string workspacePath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (string root in session.WorkspaceRoots)
        {
            if (string.Equals(root, workspacePath, comparison))
            {
                return true;
            }

            string containmentRoot = IsWorkspaceEntryPoint(root)
                ? Path.GetDirectoryName(root) ?? root
                : root;
            string relativePath = Path.GetRelativePath(containmentRoot, workspacePath);
            if (!Path.IsPathRooted(relativePath) &&
                !relativePath.Equals("..", comparison) &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWorkspaceEntryPoint(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
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
