using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Observes real target signals while preserving complete queued protocol frames.
/// </summary>
internal sealed partial class DapTestClient
{
    /// <summary>
    /// Waits for target execution and rejects operation completion or target exit without an execution signal.
    /// </summary>
    /// <param name="path">The target-created signal file.</param>
    /// <param name="requestSequence">The operation whose execution signal is expected.</param>
    /// <param name="cancellationToken">Cancels the signal and protocol observation.</param>
    /// <returns>A task that completes when target execution creates the signal.</returns>
    internal async Task WaitForTargetSignalAsync(
        string path,
        int requestSequence,
        CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path));
        watcher.Created += (_, _) => signal.TrySetResult();
        watcher.Renamed += (_, _) => signal.TrySetResult();
        watcher.Error += (_, args) => signal.TrySetException(args.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
        {
            _ = signal.TrySetResult();
        }

        while (!signal.Task.IsCompleted)
        {
            Task<JsonDocument> incoming = GetPendingMessageAsync(cancellationToken);
            _ = await Task.WhenAny(signal.Task, incoming)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (signal.Task.IsCompleted)
            {
                break;
            }

            if (!incoming.IsCompleted)
            {
                continue;
            }

            JsonDocument message = await incoming.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pendingMessage = null;
            _bufferedMessages.Enqueue(message);
            JsonElement root = message.RootElement;
            bool completed = root.GetProperty("type").GetString() == "response" &&
                root.GetProperty("request_seq").GetInt32() == requestSequence;
            bool terminated = root.GetProperty("type").GetString() == "event" &&
                root.GetProperty("event").GetString() is "exited" or "terminated";
            // Filesystem notifications and protocol reads are independent transports.
            // The target may have created its signal before this response arrived,
            // while the filesystem notification is still queued on another thread.
            if (File.Exists(path))
            {
                _ = signal.TrySetResult();
                break;
            }

            if (completed || terminated)
            {
                Assert.Fail(
                    $"The target-code operation ended before its execution signal. " +
                    $"Message: {root.GetRawText()}. Adapter diagnostics: {Diagnostics}. " +
                    $"Recent protocol messages:{Environment.NewLine}{ProtocolTranscript}");
            }

            if (_bufferedMessages.Count >= 64)
            {
                Assert.Fail(
                    $"The target emitted too many messages before its execution signal. " +
                    $"Recent protocol messages:{Environment.NewLine}{ProtocolTranscript}");
            }
        }

        await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
