using Csls.Debugger.Contracts;
using System.Collections.Concurrent;

namespace Csls.Debugger.Tests;

/// <summary>
/// Records progress from real stack requests and synchronously runs the requesting client's callback.
/// </summary>
/// <param name="onReport">The optional client action performed at an observed traversal checkpoint.</param>
internal sealed class StackWalkProgressRecorder(Action<DebugStackWalkProgress>? onReport = null)
    : IProgress<DebugStackWalkProgress>
{
    private readonly ConcurrentQueue<DebugStackWalkProgress> _updates = new();
    private readonly TaskCompletionSource<DebugStackWalkProgress> _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a stable snapshot of the received updates.
    /// </summary>
    internal DebugStackWalkProgress[] Updates => [.. _updates];

    /// <summary>
    /// Gets the first terminal notification delivered by the production stack walker.
    /// </summary>
    internal Task<DebugStackWalkProgress> Terminal => _terminal.Task;

    /// <inheritdoc />
    public void Report(DebugStackWalkProgress value)
    {
        _updates.Enqueue(value);
        onReport?.Invoke(value);
        if (value.State != DebugStackWalkState.Walking)
        {
            _terminal.TrySetResult(value);
        }
    }
}
