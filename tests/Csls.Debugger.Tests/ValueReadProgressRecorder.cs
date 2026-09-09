using Csls.Debugger.Contracts;
using System.Collections.Concurrent;

namespace Csls.Debugger.Tests;

/// <summary>
/// Records live-value progress notifications from a real worker connection.
/// </summary>
internal sealed class ValueReadProgressRecorder : IProgress<DebugValueReadProgress>
{
    private readonly ConcurrentQueue<DebugValueReadProgress> _updates = new();
    private readonly TaskCompletionSource<DebugValueReadProgress> _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a stable snapshot of received notifications.
    /// </summary>
    internal DebugValueReadProgress[] Updates => [.. _updates];

    /// <summary>
    /// Gets the first terminal notification delivered by the worker.
    /// </summary>
    internal Task<DebugValueReadProgress> Terminal => _terminal.Task;

    /// <inheritdoc />
    public void Report(DebugValueReadProgress value)
    {
        _updates.Enqueue(value);
        if (value.State != DebugValueReadState.Reading)
        {
            _terminal.TrySetResult(value);
        }
    }
}
