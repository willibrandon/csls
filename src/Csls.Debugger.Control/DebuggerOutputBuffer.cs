using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Retains a bounded cursor-addressable window of target output.
/// </summary>
internal sealed class DebuggerOutputBuffer
{
    private const int MaximumEntries = 1024;
    private const int MaximumSegmentCharacters = 8192;
    private readonly Lock _gate = new();
    private readonly Queue<DebugOutputEntry> _entries = new();
    private long _nextSequence;

    /// <summary>
    /// Adds one target-output segment to the retained window.
    /// </summary>
    internal void Add(DebugOutputCategory category, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        bool truncated = output.Length > MaximumSegmentCharacters;
        string retained = truncated ? output[^MaximumSegmentCharacters..] : output;
        lock (_gate)
        {
            _entries.Enqueue(new DebugOutputEntry(
                checked(++_nextSequence),
                category,
                retained,
                truncated));
            while (_entries.Count > MaximumEntries)
            {
                _ = _entries.Dequeue();
            }
        }
    }

    /// <summary>
    /// Gets one output page after the supplied sequence cursor.
    /// </summary>
    internal DebugOutputPage GetPage(long afterSequence, int count)
    {
        if (afterSequence < 0 || count is <= 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "afterSequence must be non-negative and count must be between 1 and 256.");
        }

        lock (_gate)
        {
            long firstRetained = _entries.TryPeek(out DebugOutputEntry? first)
                ? first.Sequence
                : _nextSequence + 1;
            long dropped = Math.Max(0, firstRetained - afterSequence - 1);
            DebugOutputEntry[] entries =
            [
                .. _entries
                    .Where(entry => entry.Sequence > afterSequence)
                    .Take(count)
            ];
            long next = entries.Length == 0 ? afterSequence : entries[^1].Sequence;
            bool hasMore = _entries.Any(entry => entry.Sequence > next);
            return new DebugOutputPage(entries, next, firstRetained, dropped, hasMore);
        }
    }
}
