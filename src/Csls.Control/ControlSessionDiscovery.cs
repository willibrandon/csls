using Csls.Control.Contracts;
using StreamJsonRpc;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace Csls.Control;

/// <summary>
/// Discovers bounded live-session state through the private per-user socket directory.
/// </summary>
public static class ControlSessionDiscovery
{
    private const int MaximumSessionSockets = 256;
    private const int MaximumSessionSocketEntries = 4_096;
    private const int MaximumConcurrentSessionConnections = 16;
    private const string SocketSuffix = ".csls.socket";
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
                .EnumerateFiles(
                    socketDirectory,
                    $"*{SocketSuffix}",
                    SearchOption.TopDirectoryOnly)
                .Take(MaximumSessionSocketEntries + 1)
        ];
        if (socketPaths.Length > MaximumSessionSocketEntries)
        {
            throw new InvalidDataException(
                $"Session discovery exceeded {MaximumSessionSocketEntries} socket entries.");
        }

        var sessions = new ControlSessionInfo?[socketPaths.Length];
        await Parallel.ForAsync(
            0,
            socketPaths.Length,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentSessionConnections
            },
            async (index, operationCancellationToken) =>
            {
                string socketPath = socketPaths[index];
                bool hasProcessId = TryGetProcessId(socketPath, out int processId);
                if (hasProcessId && !IsProcessAlive(processId))
                {
                    TryDeleteStaleSocket(socketPath);
                    return;
                }

                ControlSessionInfo? session = await TryGetSessionAsync(
                    socketPath,
                    operationCancellationToken).ConfigureAwait(false);
                if (session is not null)
                {
                    sessions[index] = session;
                }
                else if (!hasProcessId || !IsProcessAlive(processId))
                {
                    TryDeleteStaleSocket(socketPath);
                }
            }).ConfigureAwait(false);

        List<ControlSessionInfo> responsiveSessions =
        [
            .. sessions.OfType<ControlSessionInfo>()
        ];
        if (responsiveSessions.Count > MaximumSessionSockets)
        {
            throw new InvalidDataException(
                $"Session discovery exceeded {MaximumSessionSockets} responsive sessions.");
        }

        responsiveSessions.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return responsiveSessions;
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

    /// <summary>
    /// Resolves one live session containing a workspace without requiring a match.
    /// </summary>
    /// <param name="workspacePath">The workspace, project, or document path to match.</param>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>The matching session, or null when no live session contains the path.</returns>
    public static async Task<ControlSessionInfo?> TryResolveWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        string fullWorkspacePath = Path.GetFullPath(workspacePath);
        IReadOnlyList<ControlSessionInfo> discovered = await DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        ControlSessionInfo[] matches =
        [
            .. discovered.Where(session => ContainsWorkspace(session, fullWorkspacePath))
        ];
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple live csls sessions contain workspace {fullWorkspacePath}. " +
                "Specify --session.")
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

    private static bool TryGetProcessId(string socketPath, out int processId)
    {
        string fileName = Path.GetFileName(socketPath);
        if (fileName.EndsWith(SocketSuffix, StringComparison.Ordinal))
        {
            return int.TryParse(
                fileName.AsSpan(0, fileName.Length - SocketSuffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId) &&
                processId > 0;
        }

        processId = 0;
        return false;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static bool TryDeleteStaleSocket(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
