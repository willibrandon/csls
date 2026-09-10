using Csls.Debugger.Contracts;
using System.Collections.Concurrent;

namespace Csls.Debugger.Tests;

/// <summary>
/// Observes real captured-memory callbacks and applies the requesting client's cancellation policy.
/// </summary>
/// <param name="cancellation">The client's request cancellation source.</param>
/// <param name="checkpoint">The observed callback checkpoint at which to cancel.</param>
/// <param name="failure">The client observer failure to raise during reading.</param>
/// <param name="cancelCompleted">Whether to cancel after observing a completed read.</param>
/// <param name="failureState">The notification state at which to raise the client failure.</param>
internal sealed class DumpReadProgressRecorder(CancellationTokenSource cancellation, long checkpoint = 0,
    Exception? failure = null, bool cancelCompleted = false, DebugDumpReadState failureState = DebugDumpReadState.Reading)
    : IProgress<DebugDumpReadProgress>
{
    private readonly ConcurrentQueue<DebugDumpReadProgress> _updates = new();
    private readonly TaskCompletionSource<DebugDumpReadProgress> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the observed updates without exposing the concurrently written collection.
    /// </summary>
    internal DebugDumpReadProgress[] Updates => [.. _updates];

    /// <summary>
    /// Gets the actual terminal progress notification delivered through local or private RPC observation.
    /// </summary>
    internal Task<DebugDumpReadProgress> Terminal => _terminal.Task;

    /// <inheritdoc />
    public void Report(DebugDumpReadProgress value)
    {
        _updates.Enqueue(value);
        if (value.State == DebugDumpReadState.Reading && value.MemoryReads + value.ContextReads == checkpoint ||
            cancelCompleted && value.State == DebugDumpReadState.Completed)
        {
            cancellation.Cancel();
        }

        if (value.State == failureState && failure is not null)
        {
            throw failure;
        }

        if (value.State != DebugDumpReadState.Reading)
        {
            _ = _terminal.TrySetResult(value);
        }
    }
}
