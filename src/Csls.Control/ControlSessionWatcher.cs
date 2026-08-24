using Csls.Control.Contracts;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Csls.Control;

/// <summary>
/// Watches the private socket directory and reconciles bounded live-session snapshots.
/// </summary>
public static class ControlSessionWatcher
{
    private static readonly TimeSpan s_reconciliationPeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Yields an initial snapshot and subsequent snapshots after observable session changes.
    /// </summary>
    /// <param name="cancellationToken">The watch cancellation token.</param>
    /// <returns>The asynchronous sequence of bounded live-session snapshots.</returns>
    public static async IAsyncEnumerable<IReadOnlyList<ControlSessionInfo>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string socketDirectory = ControlEndpoint.EnsureSocketDirectory();
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        using var watcher = new FileSystemWatcher(socketDirectory, "*.csls.socket")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.CreationTime |
                NotifyFilters.FileName |
                NotifyFilters.LastWrite
        };
        watcher.Changed += (_, _) => signals.Writer.TryWrite(true);
        watcher.Created += (_, _) => signals.Writer.TryWrite(true);
        watcher.Deleted += (_, _) => signals.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => signals.Writer.TryWrite(true);
        watcher.Error += (_, _) => signals.Writer.TryWrite(true);
        watcher.EnableRaisingEvents = true;
        using var reconciliationTimer = new Timer(
            static state => ((ChannelWriter<bool>)state!).TryWrite(true),
            signals.Writer,
            s_reconciliationPeriod,
            s_reconciliationPeriod);

        IReadOnlyList<ControlSessionInfo>? previous = null;
        signals.Writer.TryWrite(true);
        while (await signals.Reader
            .WaitToReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            signals.Reader.TryRead(out _);
            IReadOnlyList<ControlSessionInfo> current = await ControlSessionDiscovery
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (previous is not null && HasSameState(previous, current))
            {
                continue;
            }

            previous = current;
            yield return current;
        }
    }

    private static bool HasSameState(
        IReadOnlyList<ControlSessionInfo> left,
        IReadOnlyList<ControlSessionInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            ControlSessionInfo leftSession = left[index];
            ControlSessionInfo rightSession = right[index];
            if (leftSession.ProcessId != rightSession.ProcessId ||
                leftSession.WorkspaceGeneration != rightSession.WorkspaceGeneration ||
                !string.Equals(
                    leftSession.LifecycleState,
                    rightSession.LifecycleState,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    leftSession.SocketPath,
                    rightSession.SocketPath,
                    StringComparison.Ordinal) ||
                !leftSession.WorkspaceRoots.SequenceEqual(
                    rightSession.WorkspaceRoots,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
