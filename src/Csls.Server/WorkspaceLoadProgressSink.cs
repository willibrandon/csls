using Csls.Workspaces;
using System.Threading.Channels;

namespace Csls.Server;

/// <summary>
/// Writes synchronous workspace callbacks into the ordered asynchronous progress channel.
/// </summary>
internal sealed class WorkspaceLoadProgressSink : IProgress<WorkspaceLoadProgress>
{
    private readonly ChannelWriter<WorkspaceLoadProgress> _writer;

    /// <summary>
    /// Initializes a sink for one workspace load progress sequence.
    /// </summary>
    /// <param name="writer">The progress channel writer.</param>
    internal WorkspaceLoadProgressSink(ChannelWriter<WorkspaceLoadProgress> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>
    /// Writes one ordered project completion without scheduling another callback.
    /// </summary>
    /// <param name="value">The completed project progress.</param>
    public void Report(WorkspaceLoadProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_writer.TryWrite(value))
        {
            throw new InvalidOperationException("Workspace load progress is no longer accepting reports.");
        }
    }
}
