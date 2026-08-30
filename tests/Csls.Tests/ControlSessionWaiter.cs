using Csls.Control;
using Csls.Control.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Waits for a real language-server control session to finish LSP initialization.
/// </summary>
internal static class ControlSessionWaiter
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits for the session serving one workspace to enter its running state.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace path served by the session.</param>
    /// <param name="timeout">The maximum readiness interval.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <param name="excludedProcessIds">Existing sessions that cannot satisfy this wait.</param>
    /// <param name="expectedProcessId">The exact child process expected to own the session.</param>
    /// <returns>The matching running session snapshot.</returns>
    internal static async Task<ControlSessionInfo> WaitForRunningAsync(
        string workspacePath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? excludedProcessIds = null,
        int? expectedProcessId = null)
    {
        string expectedWorkspacePath = NormalizePath(workspacePath);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(s_pollInterval);
        var observations = new StringBuilder();
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                string socketDirectory = ControlEndpoint.GetSocketDirectory();
                if (!Directory.Exists(socketDirectory))
                {
                    continue;
                }

                IEnumerable<string> socketPaths = expectedProcessId is int processId
                    ? [ControlEndpoint.GetSocketPath(processId)]
                    : Directory.EnumerateFiles(
                        socketDirectory,
                        "*.csls.socket",
                        SearchOption.TopDirectoryOnly);
                foreach (string socketPath in socketPaths)
                {
                    ControlSessionInfo? session = await TryGetSessionAsync(
                        socketPath,
                        timeoutSource.Token).ConfigureAwait(false);
                    if (session is null)
                    {
                        observations.Clear();
                        observations.Append("socket ").Append(socketPath).Append(" unavailable");
                        continue;
                    }

                    if (expectedProcessId is int requiredProcessId &&
                        session.ProcessId != requiredProcessId)
                    {
                        continue;
                    }

                    if (excludedProcessIds is not null &&
                        excludedProcessIds.Contains(session.ProcessId))
                    {
                        continue;
                    }

                    bool servesWorkspace = session.WorkspaceRoots.Any(root =>
                        PathComparer.Equals(NormalizePath(root), expectedWorkspacePath));
                    if (!servesWorkspace)
                    {
                        continue;
                    }

                    observations.Clear();
                    observations.Append("process ")
                        .Append(session.ProcessId)
                        .Append(" state ")
                        .Append(session.LifecycleState)
                        .Append(" workspace ")
                        .Append(session.WorkspacePhase);
                    if (string.Equals(
                        session.LifecycleState,
                        "Running",
                        StringComparison.Ordinal) &&
                        string.Equals(
                            session.WorkspacePhase,
                            "Ready",
                            StringComparison.Ordinal))
                    {
                        return session;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No running csls session served {expectedWorkspacePath}. " +
                $"Last observation: {observations}");
        }

        throw new InvalidOperationException("The control-session polling loop ended unexpectedly.");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string NormalizePath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"The workspace path has no root: {fullPath}");
        string currentPath = root;
        foreach (string segment in fullPath[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Join(currentPath, segment);
            var directory = new DirectoryInfo(currentPath);
            if (directory.LinkTarget is not null)
            {
                currentPath = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? currentPath;
            }
        }

        return Path.TrimEndingDirectorySeparator(currentPath);
    }

    private static async Task<ControlSessionInfo?> TryGetSessionAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        using var probeSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        probeSource.CancelAfter(s_probeTimeout);
        try
        {
            var client = new ControlRpcClient(socketPath);
            await using ConfiguredAsyncDisposable clientCleanup =
                client.ConfigureAwait(false);
            return await client.GetSessionAsync(probeSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or
            SocketException or
            TimeoutException or
            ConnectionLostException or
            InvalidDataException or
            RemoteRpcException or
            ObjectDisposedException ||
            exception is OperationCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
