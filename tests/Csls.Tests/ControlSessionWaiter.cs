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

    /// <summary>
    /// Waits for the session serving one workspace to enter its running state.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace path served by the session.</param>
    /// <param name="timeout">The maximum readiness interval.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The matching running session snapshot.</returns>
    internal static async Task<ControlSessionInfo> WaitForRunningAsync(
        string workspacePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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

                foreach (string socketPath in Directory.EnumerateFiles(
                    socketDirectory,
                    "*.csls.socket",
                    SearchOption.TopDirectoryOnly))
                {
                    ControlSessionInfo? session = await TryGetSessionAsync(
                        socketPath,
                        timeoutSource.Token).ConfigureAwait(false);
                    if (session is null)
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
                        .Append(session.LifecycleState);
                    if (string.Equals(
                        session.LifecycleState,
                        "Running",
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
        try
        {
            var client = new ControlRpcClient(socketPath);
            await using ConfiguredAsyncDisposable clientCleanup =
                client.ConfigureAwait(false);
            return await client.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or
            SocketException or
            TimeoutException or
            ConnectionLostException or
            ObjectDisposedException)
        {
            return null;
        }
    }
}
