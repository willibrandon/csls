using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Cli.Worker;

/// <summary>
/// Streams deterministic live-session changes from bounded control-socket discovery.
/// </summary>
internal static class SessionWatchCommandHost
{
    /// <summary>
    /// Watches live sessions until command cancellation closes the stream.
    /// </summary>
    /// <param name="arguments">The normalized session-watch arguments.</param>
    /// <param name="writeJson">Whether to write newline-delimited JSON envelopes.</param>
    /// <param name="cancellationToken">The watch cancellation token.</param>
    /// <returns>The process exit code after cancellation.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 2)
        {
            CliOutputWriter.WriteError(
                "invalid-request",
                "The launcher supplied an invalid session watch request.",
                writeJson);
            return 1;
        }

        var previous = new Dictionary<int, ControlSessionInfo>();
        long sequence = 0;
        bool isInitialSnapshot = true;
        await foreach (IReadOnlyList<ControlSessionInfo> sessions in
            ControlSessionWatcher.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = sessions.ToDictionary(
                static session => session.ProcessId);
            if (isInitialSnapshot)
            {
                CliOutputWriter.WriteSessionWatchEvent(
                    CreateEvent(
                        ++sequence,
                        SessionWatchEventKind.Snapshot,
                        session: null,
                        sessions),
                    writeJson);
                isInitialSnapshot = false;
            }
            else
            {
                foreach (ControlSessionInfo removed in previous.Values
                    .Where(session => !current.ContainsKey(session.ProcessId))
                    .OrderBy(static session => session.ProcessId))
                {
                    CliOutputWriter.WriteSessionWatchEvent(
                        CreateEvent(
                            ++sequence,
                            SessionWatchEventKind.Removed,
                            removed,
                            sessions),
                        writeJson);
                }

                foreach (ControlSessionInfo added in current.Values
                    .Where(session => !previous.ContainsKey(session.ProcessId))
                    .OrderBy(static session => session.ProcessId))
                {
                    CliOutputWriter.WriteSessionWatchEvent(
                        CreateEvent(
                            ++sequence,
                            SessionWatchEventKind.Added,
                            added,
                            sessions),
                        writeJson);
                }

                foreach (ControlSessionInfo updated in current.Values
                    .Where(session => previous.TryGetValue(
                        session.ProcessId,
                        out ControlSessionInfo? oldSession) &&
                        !HasSameState(oldSession, session))
                    .OrderBy(static session => session.ProcessId))
                {
                    CliOutputWriter.WriteSessionWatchEvent(
                        CreateEvent(
                            ++sequence,
                            SessionWatchEventKind.Updated,
                            updated,
                            sessions),
                        writeJson);
                }
            }

            previous = current;
        }

        return 0;
    }

    private static SessionWatchEvent CreateEvent(
        long sequence,
        SessionWatchEventKind kind,
        ControlSessionInfo? session,
        IReadOnlyList<ControlSessionInfo> sessions) => new()
        {
            Sequence = sequence,
            Kind = kind,
            Session = session,
            Sessions = sessions
        };

    private static bool HasSameState(
        ControlSessionInfo left,
        ControlSessionInfo right) =>
        left.ProcessId == right.ProcessId &&
        left.WorkspaceGeneration == right.WorkspaceGeneration &&
        string.Equals(left.LifecycleState, right.LifecycleState, StringComparison.Ordinal) &&
        string.Equals(left.SocketPath, right.SocketPath, StringComparison.Ordinal) &&
        left.WorkspaceRoots.SequenceEqual(right.WorkspaceRoots, StringComparer.Ordinal);
}
